using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class ControllerTests
{
    private static IReadOnlySet<ControllerButton> Set(params ControllerButton[] buttons) => new HashSet<ControllerButton>(buttons);

    private static bool[] Pressed(params int[] indices)
    {
        var buttons = new bool[ControllerLayout.PlayStationButtons];
        foreach (var i in indices) buttons[i] = true;
        return buttons;
    }

    private static double[] Axes(double lx = 0.5, double ly = 0.5, double rx = 0.5, double l2 = 0, double r2 = 0, double ry = 0.5) => new[] { lx, ly, rx, l2, r2, ry };

    [Theory]
    [InlineData(0x054C, 0x0CE6, ControllerFamily.PlayStation)] // DualSense
    [InlineData(0x054C, 0x0DF2, ControllerFamily.PlayStation)] // DualSense Edge
    [InlineData(0x054C, 0x09CC, ControllerFamily.PlayStation)] // DualShock 4
    [InlineData(0x054C, 0x05C4, ControllerFamily.PlayStation)]
    [InlineData(0x045E, 0x02FD, ControllerFamily.Unknown)]      // Xbox kommt als Windows-Gamepad, nicht über diese Tabelle
    [InlineData(0x054C, 0x0001, ControllerFamily.Unknown)]
    public void Family_is_recognised_from_vendor_and_product(int vendor, int product, ControllerFamily expected) =>
        Assert.Equal(expected, ControllerLayout.FamilyOf((ushort)vendor, (ushort)product));

    [Fact]
    public void PlayStation_face_buttons_map_to_cross_circle_square_triangle_names()
    {
        // Rohreihenfolge: Quadrat, Kreuz, Kreis, Dreieck
        var snapshot = ControllerLayout.FromPlayStation(Pressed(0, 1, 2, 3), new[] { 0 }, Axes());

        Assert.Equal(Set(ControllerButton.X, ControllerButton.A, ControllerButton.B, ControllerButton.Y), snapshot.Buttons);
        Assert.Equal("Kreuz", ControllerLayout.Label(ControllerButton.A, ControllerFamily.PlayStation));
        Assert.Equal("Kreis", ControllerLayout.Label(ControllerButton.B, ControllerFamily.PlayStation));
        Assert.Equal("Quadrat", ControllerLayout.Label(ControllerButton.X, ControllerFamily.PlayStation));
        Assert.Equal("Dreieck", ControllerLayout.Label(ControllerButton.Y, ControllerFamily.PlayStation));
        Assert.Equal("A", ControllerLayout.Label(ControllerButton.A, ControllerFamily.Xbox));
    }

    [Fact]
    public void PlayStation_shoulders_triggers_sticks_and_system_buttons()
    {
        var snapshot = ControllerLayout.FromPlayStation(Pressed(4, 5, 8, 9, 10, 11, 12, 13), new[] { 0 }, Axes());

        Assert.Equal(
            Set(ControllerButton.LeftBumper, ControllerButton.RightBumper, ControllerButton.View, ControllerButton.Menu,
                ControllerButton.LeftStick, ControllerButton.RightStick, ControllerButton.Guide, ControllerButton.Touchpad),
            snapshot.Buttons);
        Assert.Equal("Options", ControllerLayout.Label(ControllerButton.Menu, ControllerFamily.PlayStation));
    }

    [Fact]
    public void PlayStation_trigger_axis_counts_as_button_even_when_the_digital_button_is_silent()
    {
        var snapshot = ControllerLayout.FromPlayStation(Pressed(), new[] { 0 }, Axes(l2: 0.9, r2: 0.2));

        Assert.Equal(Set(ControllerButton.LeftTrigger), snapshot.Buttons);
    }

    [Theory]
    [InlineData(0, new ControllerButton[0])]
    [InlineData(1, new[] { ControllerButton.DpadUp })]
    [InlineData(2, new[] { ControllerButton.DpadUp, ControllerButton.DpadRight })]
    [InlineData(3, new[] { ControllerButton.DpadRight })]
    [InlineData(4, new[] { ControllerButton.DpadRight, ControllerButton.DpadDown })]
    [InlineData(5, new[] { ControllerButton.DpadDown })]
    [InlineData(6, new[] { ControllerButton.DpadDown, ControllerButton.DpadLeft })]
    [InlineData(7, new[] { ControllerButton.DpadLeft })]
    [InlineData(8, new[] { ControllerButton.DpadLeft, ControllerButton.DpadUp })]
    public void Hat_switch_positions_become_dpad_directions(int position, ControllerButton[] expected)
    {
        var snapshot = ControllerLayout.FromPlayStation(Pressed(), new[] { position }, Axes());

        Assert.Equal(Set(expected), snapshot.Buttons);
    }

    [Fact]
    public void Sticks_are_centered_with_a_small_dead_zone_and_up_or_left_is_negative()
    {
        var idle = ControllerLayout.FromPlayStation(Pressed(), new[] { 0 }, Axes(lx: 0.53, ly: 0.47));
        Assert.Equal(0, idle.LeftX);
        Assert.Equal(0, idle.LeftY);

        var pushed = ControllerLayout.FromPlayStation(Pressed(), new[] { 0 }, Axes(lx: 0.0, ly: 1.0, rx: 1.0, ry: 0.0));
        Assert.Equal(-1, pushed.LeftX, 3);
        Assert.Equal(1, pushed.LeftY, 3);
        Assert.Equal(1, pushed.RightX, 3);
        Assert.Equal(-1, pushed.RightY, 3);
    }

    [Fact]
    public void Left_stick_acts_as_dpad_beyond_the_threshold_only()
    {
        Assert.Empty(new ControllerSnapshot(Set(), LeftX: 0.5).WithStickAsDpad().Buttons);
        Assert.Equal(Set(ControllerButton.DpadLeft, ControllerButton.DpadUp), new ControllerSnapshot(Set(), LeftX: -0.9, LeftY: -0.7).WithStickAsDpad().Buttons);
        Assert.Equal(Set(ControllerButton.DpadDown, ControllerButton.A), new ControllerSnapshot(Set(ControllerButton.A), LeftY: 1).WithStickAsDpad().Buttons);
    }

    [Fact]
    public void A_controller_with_too_few_buttons_or_axes_is_a_loud_error()
    {
        Assert.Throws<InvalidDataException>(() => ControllerLayout.FromPlayStation(new bool[5], new[] { 0 }, Axes()));
        Assert.Throws<InvalidDataException>(() => ControllerLayout.FromPlayStation(Pressed(), new[] { 0 }, new[] { 0.5, 0.5 }));
    }

    [Fact]
    public void Repeater_reports_a_new_button_once_and_repeats_only_directions()
    {
        var repeater = new ControllerRepeater(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new[] { ControllerButton.A, ControllerButton.DpadDown }, repeater.Update(Set(ControllerButton.A, ControllerButton.DpadDown), t));
        Assert.Empty(repeater.Update(Set(ControllerButton.A, ControllerButton.DpadDown), t.AddMilliseconds(300)));                          // vor der ersten Pause
        Assert.Equal(new[] { ControllerButton.DpadDown }, repeater.Update(Set(ControllerButton.A, ControllerButton.DpadDown), t.AddMilliseconds(420))); // A wiederholt sich nie
        Assert.Empty(repeater.Update(Set(ControllerButton.A, ControllerButton.DpadDown), t.AddMilliseconds(480)));
        Assert.Equal(new[] { ControllerButton.DpadDown }, repeater.Update(Set(ControllerButton.A, ControllerButton.DpadDown), t.AddMilliseconds(530)));
    }

    [Fact]
    public void Repeater_counts_a_released_and_pressed_again_button_as_new()
    {
        var repeater = new ControllerRepeater();
        var t = DateTime.UtcNow;

        Assert.Single(repeater.Update(Set(ControllerButton.B), t));
        Assert.Empty(repeater.Update(Set(), t.AddMilliseconds(20)));
        Assert.Single(repeater.Update(Set(ControllerButton.B), t.AddMilliseconds(40)));
    }

    [Fact]
    public void Repeater_reset_forgets_held_buttons_so_the_next_poll_reports_them_again()
    {
        var repeater = new ControllerRepeater();
        var t = DateTime.UtcNow;
        repeater.Update(Set(ControllerButton.A), t);

        repeater.Reset();

        Assert.Single(repeater.Update(Set(ControllerButton.A), t.AddMilliseconds(20)));
    }

    [Fact]
    public void Every_player_binding_of_the_controller_has_a_description_and_matches_the_map()
    {
        foreach (var (button, effect) in ControllerPlayerMap.Overview)
        {
            Assert.NotEmpty(effect);
            Assert.NotNull(ControllerPlayerMap.For(button));
        }
        Assert.Equal((PlayerAction.PlayPause, false), ControllerPlayerMap.For(ControllerButton.A));
        Assert.Equal((PlayerAction.SeekForward, true), ControllerPlayerMap.For(ControllerButton.RightTrigger));
        Assert.Null(ControllerPlayerMap.For(ControllerButton.Guide));
    }

    // --- Tastenbelegung --------------------------------------------------------------------------------

    [Fact]
    public void Default_keys_resolve_without_conflict_and_cover_every_action()
    {
        var map = PlayerKeys.Resolve(null);

        foreach (var action in Enum.GetValues<PlayerAction>()) Assert.Contains(action, map.Values);
        Assert.Equal(PlayerAction.PlayPause, map["Space"]);
        Assert.Equal(PlayerAction.PlayPause, map["K"]);
        Assert.Equal(PlayerAction.FrameForward, map["OemPeriod"]);
        Assert.Equal(PlayerAction.AudioCycle, map["T"]);
    }

    [Fact]
    public void Every_action_has_a_german_label()
    {
        foreach (var action in Enum.GetValues<PlayerAction>()) Assert.NotEmpty(PlayerKeys.Label(action));
    }

    [Fact]
    public void Assigning_a_key_replaces_the_default_keys_of_that_action()
    {
        var changed = PlayerKeys.Assign(null, PlayerAction.Mute, "O", out var conflict);

        Assert.Null(conflict);
        var map = PlayerKeys.Resolve(changed);
        Assert.Equal(PlayerAction.Mute, map["O"]);
        Assert.DoesNotContain("M", map.Keys); // die Standardtaste ist frei
    }

    [Fact]
    public void Assigning_a_taken_key_reports_the_other_action_and_changes_nothing()
    {
        var before = new Dictionary<string, string[]>();

        var after = PlayerKeys.Assign(before, PlayerAction.Mute, "Space", out var conflict);

        Assert.Equal(PlayerAction.PlayPause, conflict);
        Assert.Empty(after);
    }

    [Fact]
    public void A_key_freed_by_a_change_can_be_used_by_another_action()
    {
        var first = PlayerKeys.Assign(null, PlayerAction.PlayPause, "Enter", out _);   // Leertaste und K werden frei
        var second = PlayerKeys.Assign(first, PlayerAction.Mute, "Space", out var conflict);

        Assert.Null(conflict);
        var map = PlayerKeys.Resolve(second);
        Assert.Equal(PlayerAction.Mute, map["Space"]);
        Assert.Equal(PlayerAction.PlayPause, map["Enter"]);
    }

    [Fact]
    public void Reset_restores_defaults_unless_another_action_took_one_of_them()
    {
        var changed = PlayerKeys.Assign(null, PlayerAction.Mute, "O", out _);
        var reset = PlayerKeys.Reset(changed, PlayerAction.Mute, out var conflict);
        Assert.Null(conflict);
        Assert.Equal(PlayerAction.Mute, PlayerKeys.Resolve(reset)["M"]);

        var moved = PlayerKeys.Assign(changed, PlayerAction.Stats, "M", out _); // M ist durch die Änderung frei
        var blocked = PlayerKeys.Reset(moved, PlayerAction.Mute, out var other);
        Assert.Equal(PlayerAction.Stats, other);
        Assert.Equal(moved.Count, blocked.Count);
    }

    [Fact]
    public void A_hand_edited_double_binding_unknown_action_or_empty_key_is_a_loud_error()
    {
        Assert.Throws<InvalidDataException>(() => PlayerKeys.Resolve(new Dictionary<string, string[]> { ["Mute"] = new[] { "Space" } }));
        Assert.Throws<InvalidDataException>(() => PlayerKeys.Resolve(new Dictionary<string, string[]> { ["Fliegen"] = new[] { "Q" } }));
        Assert.Throws<InvalidDataException>(() => PlayerKeys.Resolve(new Dictionary<string, string[]> { ["Mute"] = new[] { " " } }));
    }
}
