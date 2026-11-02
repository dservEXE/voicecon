namespace Voicecon;
public sealed partial class MainForm
{
    private readonly SlimSlider microphoneGain = new(), participantVoice = new(), participantShare = new();
    private readonly Label microphoneGainValue = new(), participantVoiceValue = new(), participantShareValue = new();
    private readonly ThemedComboBox participantChoice = new();
    private ThemedScrollHost? settingsScroller;
    private Control? participantHeading;
    private bool loadingParticipantAudio;
    private string audioParticipantSignature = "";
    private void AddMicrophoneGain(TableLayoutPanel grid)
    {
        var row = Columns(-1, 70); row.Controls.Add(TextLabel("Усиление моего микрофона", 9, TextTone.Muted), 0, 0);
        PrepareLabel(microphoneGainValue, TextTone.Accent); microphoneGainValue.TextAlign = ContentAlignment.MiddleRight; microphoneGainValue.Text = $"{settings.MicrophoneGain}%"; row.Controls.Add(microphoneGainValue, 1, 0); AddRow(grid, row, 30);
        microphoneGain.Maximum = AudioLevels.MaximumPercent; microphoneGain.Value = settings.MicrophoneGain; AddRow(grid, microphoneGain, 28);
        AddRow(grid, TextLabel("100% — исходный уровень · усиление до 300%", 8.5f, TextTone.Muted), 24);
        microphoneGain.ValueChanged += (_, _) => { microphoneGainValue.Text = $"{microphoneGain.Value}%"; if (!previewMode) { settings.MicrophoneGain = microphoneGain.Value; room?.ApplyAudioSettings(); SaveSettings(); } };
    }
    private void AddParticipantAudioSettings(TableLayoutPanel grid)
    {
        AddRow(grid, Space(), 12); participantHeading = TextLabel("Громкость участников", 12, TextTone.Normal, true); AddRow(grid, participantHeading, 30);
        PrepareCombo(participantChoice); AddRow(grid, participantChoice, 36);
        void AddLevel(string title, SlimSlider slider, Label value)
        {
            var row = Columns(-1, 70); row.Controls.Add(TextLabel(title, 9, TextTone.Muted), 0, 0); PrepareLabel(value, TextTone.Accent); value.TextAlign = ContentAlignment.MiddleRight; row.Controls.Add(value, 1, 0); AddRow(grid, row, 28);
            slider.Maximum = AudioLevels.MaximumPercent; slider.Value = 100; AddRow(grid, slider, 28);
        }
        AddLevel("Голос выбранного участника", participantVoice, participantVoiceValue); AddLevel("Звук его демонстрации", participantShare, participantShareValue);
        AddRow(grid, TextLabel("Только для вас · 0% — без звука · до 300%\nНастройки участников действуют в текущем звонке.", 8.5f, TextTone.Muted), 40);
        participantChoice.SelectedIndexChanged += (_, _) => LoadParticipantLevels();
        participantVoice.ValueChanged += (_, _) => ChangeParticipantLevels(); participantShare.ValueChanged += (_, _) => ChangeParticipantLevels(); RefreshParticipantAudio();
    }
    private void RefreshParticipantAudio()
    {
        var participants = room is { IsDisposed: false } current ? current.Participants : [];
        var signature = string.Join("|", participants.Select(p => p.Id + p.Name));
        if (signature != audioParticipantSignature || participantChoice.Items.Count == 0)
        {
            audioParticipantSignature = signature; var selected = (participantChoice.SelectedItem as VoiceRoom.ParticipantAudio)?.Id;
            loadingParticipantAudio = true;
            try
            {
                participantChoice.Items.Clear(); participantChoice.Items.AddRange(participants.Cast<object>().ToArray());
                if (participants.Count == 0) { participantChoice.Items.Add("Участники появятся во время звонка"); participantChoice.SelectedIndex = 0; }
                else participantChoice.SelectedIndex = Math.Max(0, participants.ToList().FindIndex(p => p.Id == selected));
            }
            finally { loadingParticipantAudio = false; }
        }
        LoadParticipantLevels();
    }
    private void LoadParticipantLevels()
    {
        if (loadingParticipantAudio) return;
        var selected = participantChoice.SelectedItem as VoiceRoom.ParticipantAudio;
        var peer = room?.Participants.FirstOrDefault(p => p.Id == selected?.Id);
        loadingParticipantAudio = true;
        try
        {
            participantChoice.Enabled = participantVoice.Enabled = participantShare.Enabled = peer is not null;
            participantVoice.Value = peer?.VoicePercent ?? 100; participantShare.Value = peer?.SharePercent ?? 100;
            participantVoiceValue.Text = $"{participantVoice.Value}%"; participantShareValue.Text = $"{participantShare.Value}%";
        }
        finally { loadingParticipantAudio = false; }
    }
    private void ChangeParticipantLevels()
    {
        if (loadingParticipantAudio) return;
        participantVoiceValue.Text = $"{participantVoice.Value}%"; participantShareValue.Text = $"{participantShare.Value}%";
        if (participantChoice.SelectedItem is VoiceRoom.ParticipantAudio peer) room?.SetParticipantVolume(peer.Id, participantVoice.Value, participantShare.Value);
    }
    private void ShowParticipantSettings()
    {
        ShowPage(true); RefreshParticipantAudio(); if (participantHeading is not null) settingsScroller?.ScrollTo(participantHeading);
    }
}
