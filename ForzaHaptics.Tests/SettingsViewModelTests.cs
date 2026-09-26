using ForzaHaptics.Gui.ViewModels;

namespace ForzaHaptics.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void ReadOnlyDisablesEveryFieldAndPreservesGroupStateWhenUnlocked()
    {
        var config = new AppConfig();
        config.Road.Enabled = false;
        var settings = new SettingsViewModel(config) { IsReadOnly = true };
        Assert.Equal(3, settings.Tabs.Count);
        Assert.All(settings.Tabs.SelectMany(tab => tab.Groups).SelectMany(group => group.Fields),
            field => Assert.False(field.IsEnabled));
        var enabled = Assert.IsType<BoolSettingFieldViewModel>(Field(settings, "Road texture", "Enabled"));
        enabled.Value = true;
        Assert.False(settings.Working.Road.Enabled);
        Assert.False(settings.IsDirty);
        settings.IsReadOnly = false;
        Assert.True(enabled.IsEnabled);
        Assert.False(Field(settings, "Road texture", "Gain").IsEnabled);
        enabled.Value = true;
        Assert.True(settings.Working.Road.Enabled);
        Assert.True(Field(settings, "Road texture", "Gain").IsEnabled);
    }

    [Fact]
    public void TriggerSettingsExposeVibrationWithoutResistanceCurveControls()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var fields = Tab(settings, UiTabs.Triggers).Groups.SelectMany(group => group.Fields).ToArray();
        foreach (string obsolete in new[] { "ResistanceEnabled", "MinResistance", "MaxResistance", "StartZone", "ResistanceSource" })
            Assert.DoesNotContain(fields, field => field.PropertyName == obsolete);
        Assert.Contains(fields, field => field.PropertyName == nameof(ThrottleTriggerConfig.SlipEnabled));
        Assert.Contains(fields, field => field.PropertyName == nameof(ThrottleTriggerConfig.LateralSlipEnabled));
    }

    [Fact]
    public void BuildsTabsGroupsAndEverySupportedEditorType()
    {
        var settings = new SettingsViewModel(new AppConfig());

        Assert.Equal(
            new[] { UiTabs.General, UiTabs.Vibration, UiTabs.Triggers },
            settings.Tabs.Select(tab => tab.Title));

        Assert.Equal(
            new[] { "Connection", "Strength and frequencies", "Compressor" },
            Tab(settings, UiTabs.General).Groups.Select(group => group.Title));
        Assert.Contains(Tab(settings, UiTabs.Vibration).Groups, group => group.Title == "Road texture");
        Assert.Contains(Tab(settings, UiTabs.Triggers).Groups, group => group.Title == "R2 — throttle");

        Assert.IsType<TextSettingFieldViewModel>(Field(settings, "Connection", nameof(AppConfig.Port)));
        Assert.IsType<ListSettingFieldViewModel>(Field(settings, "Connection", nameof(AppConfig.ForwardTo)));
        Assert.IsType<ChoiceSettingFieldViewModel>(Field(settings, "Connection", nameof(AppConfig.Output)));
        Assert.IsType<SliderSettingFieldViewModel>(Field(settings, "Connection", nameof(AppConfig.UsbLatencyMs)));
        Assert.IsType<ListSettingFieldViewModel>(Field(settings, "Connection", nameof(AppConfig.UsbHapticChannels)));
        Assert.IsType<BoolSettingFieldViewModel>(
            Field(settings, "Strength and frequencies", nameof(AppConfig.SwapLeftRight)));
        Assert.IsType<ChoiceSettingFieldViewModel>(
            Field(settings, "R2 — throttle", nameof(ThrottleTriggerConfig.SlipMode)));
    }

    [Fact]
    public void ChoiceFieldsExposeMetadataAndUpdateWorkingCopy()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var output = Assert.IsType<ChoiceSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.Output)));
        var triggerMode = Assert.IsType<ChoiceSettingFieldViewModel>(
            Field(settings, "R2 — throttle", nameof(ThrottleTriggerConfig.SlipMode)));

        Assert.Equal(new[] { "auto", "usb", "bt" }, output.Choices);
        Assert.Equal(new[] { "Continuous", "Repeated" }, triggerMode.Choices);

        output.SelectedValue = "bt";
        triggerMode.SelectedValue = "Repeated";

        Assert.Equal("bt", settings.Working.Output);
        Assert.Equal(TriggerSlipMode.Repeated, settings.Working.Triggers.Throttle.SlipMode);
        Assert.True(settings.IsDirty);
    }

    [Fact]
    public void NumericTextRejectsInvalidModelValueAndRecovers()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var port = Assert.IsType<TextSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.Port)));

        port.Text = "not-a-number";
        Assert.False(port.TryCommitText());
        Assert.Equal("Enter a valid number.", port.ErrorMessage);
        Assert.True(settings.HasValidationErrors);
        Assert.Equal(5310, settings.Working.Port);

        port.Text = "0";
        Assert.False(port.TryCommitText());
        Assert.Contains("between 1 and 65535", port.ErrorMessage);
        Assert.Equal(5310, settings.Working.Port);

        port.Text = "5522";
        Assert.True(port.TryCommitText());
        Assert.False(port.HasError);
        Assert.False(settings.HasValidationErrors);
        Assert.Equal(5522, settings.Working.Port);
    }

    [Fact]
    public void SliderParsesCommaSnapsAndClamps()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var gain = Assert.IsType<SliderSettingFieldViewModel>(
            Field(settings, "Strength and frequencies", nameof(AppConfig.MasterGain)));

        gain.Text = "1,23";
        Assert.True(gain.TryCommitText());
        Assert.Equal(1.25, gain.Value, precision: 6);
        Assert.Equal("1.25", gain.Text);
        Assert.Equal(1.25f, settings.Working.MasterGain);

        gain.Value = -100;
        Assert.Equal(0, gain.Value);
        Assert.Equal(0f, settings.Working.MasterGain);

        gain.Value = 100;
        Assert.Equal(4, gain.Value);
        Assert.Equal(4f, settings.Working.MasterGain);
    }

    [Fact]
    public void ListFieldsParseSupportedSeparatorsAndRejectInvalidContent()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var forwardTo = Assert.IsType<ListSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.ForwardTo)));
        var channels = Assert.IsType<ListSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.UsbHapticChannels)));

        forwardTo.Text = "127.0.0.1:5300; 10.0.0.2:5400";
        Assert.True(forwardTo.TryCommitText());
        Assert.Equal(new[] { "127.0.0.1:5300", "10.0.0.2:5400" }, settings.Working.ForwardTo);
        Assert.Equal("127.0.0.1:5300, 10.0.0.2:5400", forwardTo.Text);

        channels.Text = "2, nope";
        Assert.False(channels.TryCommitText());
        Assert.Contains("not a valid integer", channels.ErrorMessage);
        Assert.Equal(new[] { 2, 3 }, settings.Working.UsbHapticChannels);

        channels.Text = "1 2 3";
        Assert.False(channels.TryCommitText());
        Assert.Contains("must contain 2 numbers", channels.ErrorMessage);
        Assert.Equal(new[] { 2, 3 }, settings.Working.UsbHapticChannels);

        channels.Text = "4; 5";
        Assert.True(channels.TryCommitText());
        Assert.Equal(new[] { 4, 5 }, settings.Working.UsbHapticChannels);
        Assert.Equal("4, 5", channels.Text);
        Assert.False(settings.HasValidationErrors);
    }

    [Fact]
    public void ChangesAreLiveAndTrackDirtyAndRestartState()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var events = new List<SettingChangedEventArgs>();
        settings.Changed += (_, eventArgs) => events.Add(eventArgs);
        var port = Assert.IsType<TextSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.Port)));

        port.Text = "5500";
        Assert.True(port.TryCommitText());

        SettingChangedEventArgs changed = Assert.Single(events);
        Assert.Same(port, changed.Field);
        Assert.True(changed.RequiresRestart);
        Assert.True(settings.IsDirty);
        Assert.True(settings.RestartRequired);
        Assert.Equal(5500, settings.Working.Port);

        settings.MarkSaved();
        settings.MarkOutputRestarted();

        Assert.False(settings.IsDirty);
        Assert.False(settings.RestartRequired);
    }

    [Fact]
    public void NonRestartChangeDoesNotSetRestartFlag()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var swap = Assert.IsType<BoolSettingFieldViewModel>(
            Field(settings, "Strength and frequencies", nameof(AppConfig.SwapLeftRight)));
        SettingChangedEventArgs? observed = null;
        settings.Changed += (_, eventArgs) => observed = eventArgs;

        swap.Value = true;

        Assert.NotNull(observed);
        Assert.Same(swap, observed.Field);
        Assert.False(observed.RequiresRestart);
        Assert.True(settings.IsDirty);
        Assert.False(settings.RestartRequired);
        Assert.True(settings.Working.SwapLeftRight);
    }

    [Fact]
    public void EnabledToggleControlsItsGroupFields()
    {
        var settings = new SettingsViewModel(new AppConfig());
        SettingsGroupViewModel road = Group(settings, "Road texture");
        var enabled = Assert.IsType<BoolSettingFieldViewModel>(
            road.Fields.Single(field => field.PropertyName == nameof(RoadConfig.Enabled)));

        Assert.True(road.IsContentEnabled);
        Assert.All(road.Fields, field => Assert.True(field.IsEnabled));

        enabled.Value = false;

        Assert.False(road.IsContentEnabled);
        Assert.True(enabled.IsEnabled);
        Assert.All(road.Fields.Where(field => field != enabled), field => Assert.False(field.IsEnabled));
        Assert.False(settings.Working.Road.Enabled);

        enabled.Value = true;

        Assert.True(road.IsContentEnabled);
        Assert.All(road.Fields, field => Assert.True(field.IsEnabled));
    }

    [Fact]
    public void LoadUsesDeepCopyRefreshesFieldsAndResetsTransientState()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var port = Assert.IsType<TextSettingFieldViewModel>(
            Field(settings, "Connection", nameof(AppConfig.Port)));
        var roadEnabled = Assert.IsType<BoolSettingFieldViewModel>(
            Field(settings, "Road texture", nameof(RoadConfig.Enabled)));

        port.Text = "5500";
        Assert.True(port.TryCommitText());
        port.Text = "invalid";
        Assert.False(port.TryCommitText());
        Assert.True(settings.IsDirty);
        Assert.True(settings.RestartRequired);
        Assert.True(settings.HasValidationErrors);

        var replacement = new AppConfig
        {
            Port = 6200,
            ForwardTo = new List<string> { "127.0.0.1:6201" },
            Road = new RoadConfig { Enabled = false, Gain = 0.25f },
        };
        settings.Load(replacement);

        Assert.NotSame(replacement, settings.Working);
        Assert.NotSame(replacement.Road, settings.Working.Road);
        Assert.Equal(6200, settings.Working.Port);
        Assert.Equal("6200", port.Text);
        Assert.False(roadEnabled.Value);
        Assert.False(Group(settings, "Road texture").IsContentEnabled);
        Assert.False(settings.IsDirty);
        Assert.False(settings.RestartRequired);
        Assert.False(settings.HasValidationErrors);

        replacement.Port = 6300;
        replacement.ForwardTo.Add("127.0.0.1:6202");
        Assert.Equal(6200, settings.Working.Port);
        Assert.Single(settings.Working.ForwardTo);
    }

    [Fact]
    public void LoadDoesNotEmitChangedEvents()
    {
        var settings = new SettingsViewModel(new AppConfig());
        int changeCount = 0;
        settings.Changed += (_, _) => changeCount++;

        settings.Load(new AppConfig { Port = 6000, MasterGain = 1.5f });

        Assert.Equal(0, changeCount);
        Assert.False(settings.IsDirty);
        Assert.False(settings.RestartRequired);
    }

    private static SettingsTabViewModel Tab(SettingsViewModel settings, string title) =>
        settings.Tabs.Single(tab => tab.Title == title);

    private static SettingsGroupViewModel Group(SettingsViewModel settings, string title) =>
        settings.Tabs.SelectMany(tab => tab.Groups).Single(group => group.Title == title);

    private static SettingFieldViewModel Field(SettingsViewModel settings, string groupTitle, string propertyName) =>
        Group(settings, groupTitle).Fields.Single(field => field.PropertyName == propertyName);
}
