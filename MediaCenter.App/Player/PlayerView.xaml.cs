using System.Windows.Controls;

namespace MediaCenter.App.Player;

/// <summary>Schwarze Fläche mit dem nativen Fenster, in das mpv rendert. Die Bedienung liegt im <see cref="PlayerOverlay"/>.</summary>
public partial class PlayerView : UserControl
{
    public PlayerView() => InitializeComponent();

    public MpvHost VideoHost => Host;
}
