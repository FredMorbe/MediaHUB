using System.Runtime.InteropServices;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Ein aktuell angeschlossenes Laufwerk (Volume).</summary>
/// <param name="Serial">Volume-Seriennummer als Hex-String; stabil über Laufwerksbuchstaben und PCs hinweg.</param>
/// <param name="RootPath">Aktueller Einhängepunkt, z. B. "E:\".</param>
public sealed record VolumeInfo(string Serial, string Label, string FileSystem, long Capacity, string RootPath);

public interface IVolumeProvider
{
    IReadOnlyList<VolumeInfo> GetMountedVolumes();
}

/// <summary>Liest feste und Wechsel-Laufwerke über die Windows-API. Netzlaufwerke werden nicht erfasst.</summary>
public sealed class WindowsVolumeProvider : IVolumeProvider
{
    public IReadOnlyList<VolumeInfo> GetMountedVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Nicht bereite Laufwerke (leerer Kartenleser, gesperrtes BitLocker-Volume) zählen als nicht verbunden.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;

            var label = new StringBuilder(261);
            var fileSystem = new StringBuilder(261);
            var root = drive.RootDirectory.FullName;
            if (!GetVolumeInformationW(root, label, label.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity))
            {
                throw new IOException($"GetVolumeInformation für {root} fehlgeschlagen (Win32-Fehler {Marshal.GetLastWin32Error()}).");
            }
            result.Add(new VolumeInfo(serial.ToString("X8"), label.ToString(), fileSystem.ToString(), drive.TotalSize, root));
        }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
