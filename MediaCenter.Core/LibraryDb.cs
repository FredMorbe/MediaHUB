using Microsoft.Data.Sqlite;

namespace MediaCenter.Core;

/// <summary>
/// SQLite-Datenbank der Mediathek. Zwei Schichten: abgeleitete Daten (Dateien, ffprobe, Parser-Ergebnis)
/// werden beim Scan überschrieben, Nutzerdaten (Titel, Genres, gesperrte Zuordnungen, Fortschritt) nie.
/// </summary>
public sealed partial class LibraryDb : IDisposable
{
    private const int SchemaVersion = 11;

    private readonly SqliteConnection _connection;
    private SqliteTransaction? _tx;

    public LibraryDb(string dbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Run("PRAGMA foreign_keys = ON;");
        Migrate();
    }

    private void Migrate()
    {
        var version = Scalar<int>("PRAGMA user_version;");
        if (version == SchemaVersion) return;
        if (version is not (0 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10))
        {
            throw new InvalidOperationException(
                $"Datenbank hat Schema {version}, erwartet wird {SchemaVersion}. Ältere Entwicklungsstände (1) werden nicht migriert (Datei löschen).");
        }

        InTransaction(() =>
        {
            if (version == 0) Run("""
                CREATE TABLE volumes (
                    id INTEGER PRIMARY KEY,
                    serial TEXT NOT NULL,
                    label TEXT NOT NULL,
                    file_system TEXT NOT NULL,
                    capacity INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    last_root TEXT,
                    last_seen_utc TEXT
                );
                CREATE INDEX volumes_serial ON volumes(serial);

                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY,
                    volume_id INTEGER NOT NULL REFERENCES volumes(id),
                    kind TEXT NOT NULL CHECK (kind IN ('folder', 'file')),
                    rel_path TEXT NOT NULL COLLATE NOCASE,
                    added_utc TEXT NOT NULL,
                    UNIQUE (volume_id, rel_path)
                );

                CREATE TABLE files (
                    id INTEGER PRIMARY KEY,
                    volume_id INTEGER NOT NULL REFERENCES volumes(id),
                    rel_path TEXT NOT NULL COLLATE NOCASE,
                    size INTEGER NOT NULL,
                    mtime_utc TEXT NOT NULL,
                    missing INTEGER NOT NULL DEFAULT 0,
                    duration_sec REAL,
                    container TEXT,
                    width INTEGER,
                    height INTEGER,
                    video_codec TEXT,
                    is_hdr INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (volume_id, rel_path)
                );

                CREATE TABLE streams (
                    file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    idx INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    codec TEXT,
                    profile TEXT,
                    channels INTEGER,
                    language TEXT,
                    title TEXT,
                    is_default INTEGER NOT NULL,
                    is_forced INTEGER NOT NULL,
                    PRIMARY KEY (file_id, idx)
                );

                CREATE TABLE titles (
                    id INTEGER PRIMARY KEY,
                    kind TEXT NOT NULL CHECK (kind IN ('movie', 'series')),
                    name TEXT NOT NULL,
                    year INTEGER,
                    created_by TEXT NOT NULL CHECK (created_by IN ('scan', 'user'))
                );

                -- Normalisierte Namen, die auf einen Titel zeigen; so überlebt die Zuordnung ein Umbenennen oder Zusammenführen.
                CREATE TABLE title_keys (
                    match_key TEXT PRIMARY KEY COLLATE NOCASE,
                    title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE
                );

                CREATE TABLE videos (
                    id INTEGER PRIMARY KEY,
                    file_id INTEGER NOT NULL UNIQUE REFERENCES files(id) ON DELETE CASCADE,
                    parsed_kind TEXT NOT NULL CHECK (parsed_kind IN ('movie', 'episode')),
                    parsed_title TEXT NOT NULL,
                    parsed_year INTEGER,
                    parsed_season INTEGER,
                    parsed_episode INTEGER,
                    parsed_certain INTEGER NOT NULL,
                    title_id INTEGER REFERENCES titles(id),
                    season INTEGER,
                    episode INTEGER,
                    locked INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX videos_title ON videos(title_id, season, episode);

                CREATE TABLE genres (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE
                );

                CREATE TABLE title_genres (
                    title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                    genre_id INTEGER NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
                    PRIMARY KEY (title_id, genre_id)
                );

                CREATE TABLE playback (
                    video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                    position_sec REAL NOT NULL,
                    watched INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """);

            // Schema 3: Cover. Gehört zu den Nutzerdaten, soweit source = 'user'.
            if (version < 3) Run("""
                CREATE TABLE artwork (
                    owner_kind TEXT NOT NULL CHECK (owner_kind IN ('title', 'video')),
                    owner_id INTEGER NOT NULL,
                    kind TEXT NOT NULL CHECK (kind IN ('poster', 'landscape')),
                    source TEXT NOT NULL CHECK (source IN ('user', 'sidecar', 'embedded', 'frame')),
                    file TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (owner_kind, owner_id, kind)
                );
                """);

            // Schema 4: Musik (Tags sind abgeleitete Daten und werden beim Scan überschrieben), Radio und Podcasts (Nutzerdaten).
            if (version < 4) Run("""
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    file_id INTEGER NOT NULL UNIQUE REFERENCES files(id) ON DELETE CASCADE,
                    title TEXT NOT NULL,
                    artist TEXT,
                    album_artist TEXT,
                    album TEXT,
                    track_no INTEGER,
                    disc_no INTEGER,
                    year INTEGER,
                    genre TEXT
                );

                CREATE TABLE album_art (
                    album_key TEXT PRIMARY KEY,
                    source TEXT NOT NULL CHECK (source IN ('sidecar', 'embedded')),
                    file TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE radio_stations (
                    id INTEGER PRIMARY KEY,
                    station_uuid TEXT UNIQUE,
                    name TEXT NOT NULL,
                    url TEXT NOT NULL UNIQUE,
                    favicon_url TEXT,
                    favicon_file TEXT,
                    country TEXT,
                    tags TEXT,
                    codec TEXT,
                    bitrate INTEGER,
                    added_utc TEXT NOT NULL
                );

                CREATE TABLE podcasts (
                    id INTEGER PRIMARY KEY,
                    feed_url TEXT NOT NULL UNIQUE,
                    title TEXT NOT NULL,
                    author TEXT,
                    description TEXT,
                    image_url TEXT,
                    image_file TEXT,
                    last_refreshed_utc TEXT,
                    added_utc TEXT NOT NULL
                );

                CREATE TABLE podcast_episodes (
                    id INTEGER PRIMARY KEY,
                    podcast_id INTEGER NOT NULL REFERENCES podcasts(id) ON DELETE CASCADE,
                    guid TEXT NOT NULL,
                    title TEXT NOT NULL,
                    published_utc TEXT,
                    audio_url TEXT NOT NULL,
                    duration_sec REAL,
                    description TEXT,
                    UNIQUE (podcast_id, guid)
                );
                CREATE INDEX podcast_episodes_published ON podcast_episodes(podcast_id, published_utc);

                CREATE TABLE podcast_progress (
                    episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE,
                    position_sec REAL NOT NULL,
                    played INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """);

            // Schema 5: Zeitversatz, Größe und Lage der Untertitel pro Video (Nutzerdaten; fehlt die Zeile, gelten die Standardwerte).
            if (version < 5) Run("""
                CREATE TABLE subtitle_settings (
                    video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                    delay_sec REAL NOT NULL,
                    scale REAL NOT NULL,
                    position INTEGER NOT NULL
                );
                """);

            // Schema 6: Farbeinstellungen pro Video (Nutzerdaten; fehlt die Zeile, gelten die Standardwerte).
            if (version < 6) Run("""
                CREATE TABLE color_settings (
                    video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                    brightness INTEGER NOT NULL,
                    contrast INTEGER NOT NULL,
                    saturation INTEGER NOT NULL,
                    hue INTEGER NOT NULL,
                    gamma INTEGER NOT NULL,
                    temperature INTEGER NOT NULL,
                    tint INTEGER NOT NULL,
                    sharpness INTEGER NOT NULL,
                    invert INTEGER NOT NULL
                );
                """);

            // Schema 7: Cover aus dem Internet. SQLite kann eine CHECK-Bedingung nicht ändern, deshalb wird artwork neu aufgebaut
            // (gleiche Spalten, source darf jetzt auch 'online' sein). online_lookups merkt sich ergebnislose Suchen, damit nicht
            // bei jedem Durchlauf erneut gefragt wird.
            if (version < 7) Run("""
                CREATE TABLE artwork_new (
                    owner_kind TEXT NOT NULL CHECK (owner_kind IN ('title', 'video')),
                    owner_id INTEGER NOT NULL,
                    kind TEXT NOT NULL CHECK (kind IN ('poster', 'landscape')),
                    source TEXT NOT NULL CHECK (source IN ('user', 'sidecar', 'embedded', 'online', 'frame')),
                    file TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (owner_kind, owner_id, kind)
                );
                INSERT INTO artwork_new SELECT owner_kind, owner_id, kind, source, file, updated_utc FROM artwork;
                DROP TABLE artwork;
                ALTER TABLE artwork_new RENAME TO artwork;

                CREATE TABLE online_lookups (
                    title_id INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
                    provider TEXT NOT NULL,
                    query TEXT NOT NULL,
                    found INTEGER NOT NULL,
                    checked_utc TEXT NOT NULL,
                    PRIMARY KEY (title_id, provider)
                );
                """);

            // Schema 8: Dateien, die der Nutzer aus der Bibliothek entfernt hat (Nutzerdaten). Sie liegen noch auf der Platte; ein Scan überspringt sie.
            if (version < 8) Run("""
                CREATE TABLE removed_files (
                    volume_id INTEGER NOT NULL REFERENCES volumes(id),
                    rel_path TEXT NOT NULL COLLATE NOCASE,
                    removed_utc TEXT NOT NULL,
                    PRIMARY KEY (volume_id, rel_path)
                );
                """);

            // Schema 9: Album-Cover aus dem Internet. album_art wird wie artwork in Schema 7 neu aufgebaut (source darf jetzt auch
            // 'online' sein); album_lookups merkt sich ergebnislose Suchen je Album und Anbieter.
            if (version < 9) Run("""
                CREATE TABLE album_art_new (
                    album_key TEXT PRIMARY KEY,
                    source TEXT NOT NULL CHECK (source IN ('sidecar', 'embedded', 'online')),
                    file TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO album_art_new SELECT album_key, source, file, updated_utc FROM album_art;
                DROP TABLE album_art;
                ALTER TABLE album_art_new RENAME TO album_art;

                CREATE TABLE album_lookups (
                    album_key TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    query TEXT NOT NULL,
                    found INTEGER NOT NULL,
                    checked_utc TEXT NOT NULL,
                    PRIMARY KEY (album_key, provider)
                );
                """);
            // Schema 10: Playlists (Nutzerdaten). position ordnet die Titel einer Playlist; ein Titel steht höchstens einmal darin.
            if (version < 10) Run("""
                CREATE TABLE playlists (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_utc TEXT NOT NULL
                );

                CREATE TABLE playlist_items (
                    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                    track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                    position INTEGER NOT NULL,
                    added_utc TEXT NOT NULL,
                    PRIMARY KEY (playlist_id, track_id)
                );
                CREATE INDEX playlist_items_position ON playlist_items(playlist_id, position);
                """);

            // Schema 11: Meine Liste, vorgemerkte Filme und Serien (Nutzerdaten).
            if (version < 11) Run("""
                CREATE TABLE watchlist (
                    title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE,
                    added_utc TEXT NOT NULL
                );
                """);
            Run($"PRAGMA user_version = {SchemaVersion};");
        });
    }

    // --- Hilfsmethoden -------------------------------------------------------------------------------------

    private void InTransaction(Action body) => InTransaction(() => { body(); return 0; });

    private T InTransaction<T>(Func<T> body)
    {
        if (_tx is not null) return body(); // an äußere Transaktion anschließen

        _tx = _connection.BeginTransaction();
        try
        {
            var result = body();
            _tx.Commit();
            return result;
        }
        catch
        {
            _tx.Rollback();
            throw;
        }
        finally
        {
            _tx.Dispose();
            _tx = null;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Run(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull) return default!;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O");

    public void Dispose() => _connection.Dispose();
}

internal static class ReaderExtensions
{
    public static int? GetInt32OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static double? GetDoubleOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
