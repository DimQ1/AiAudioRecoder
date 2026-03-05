using System;
using System.Collections.ObjectModel;
using System.Linq;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;
using Microsoft.Maui.Controls;

namespace AiAudioRecoder.Views
{
    public partial class AnalysisSettingsPage : ContentPage
    {
        private readonly TextAnalysisService _analysisService;
        private TextAnalysisOptions _options;
        private readonly ObservableCollection<LocalTextAnalysisModel> _localModels = new();
        private bool _initialized;

        public AnalysisSettingsPage(TextAnalysisService analysisService)
        {
            InitializeComponent();
            _analysisService = analysisService;
            _options = _analysisService.GetOptions();
            InitializeUi();
        }

        private void InitializeUi()
        {
            _initialized = false;

            foreach (var model in _analysisService.LocalModels)
            {
                _localModels.Add(model);
            }

            LocalModelPicker.ItemsSource = _localModels;
            SelectLocalModel(_options.LocalModelId);

            UseLocalSwitch.IsToggled = _options.UseLocalModel;
            ApiEndpointEntry.Text = _options.ApiEndpoint;
            ApiKeyEntry.Text = _options.ApiKey;
            RemoteModelEntry.Text = _options.RemoteModel;
            PromptEditor.Text = _options.PromptTemplate;

            UpdateRemoteInputsVisibility(_options.UseLocalModel);

            _initialized = true;
        }

        private void SelectLocalModel(string modelId)
        {
            if (_localModels.Count == 0)
            {
                return;
            }

            var selected = _localModels.FirstOrDefault(m =>
                string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                ?? _localModels.FirstOrDefault();
            LocalModelPicker.SelectedItem = selected;
        }

        private void UpdateRemoteInputsVisibility(bool useLocal)
        {
            var visible = !useLocal;
            ApiEndpointEntry.IsVisible = visible;
            ApiEndpointLabel.IsVisible = visible;
            ApiKeyEntry.IsVisible = visible;
            ApiKeyLabel.IsVisible = visible;
            RemoteModelEntry.IsVisible = visible;
            RemoteModelLabel.IsVisible = visible;
        }

        private void OnUseLocalSwitchToggled(object? sender, ToggledEventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            _options.UseLocalModel = e.Value;
            UpdateRemoteInputsVisibility(e.Value);
        }

        private void OnLocalModelChanged(object? sender, EventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            if (LocalModelPicker.SelectedItem is LocalTextAnalysisModel model)
            {
                _options.LocalModelId = model.Id;
            }
        }

        private void OnSaveClicked(object? sender, EventArgs e)
        {
            _options.UseLocalModel = UseLocalSwitch.IsToggled;
            _options.ApiEndpoint = ApiEndpointEntry.Text?.Trim() ?? string.Empty;
            _options.ApiKey = ApiKeyEntry.Text?.Trim() ?? string.Empty;
            _options.RemoteModel = RemoteModelEntry.Text?.Trim() ?? string.Empty;
            _options.PromptTemplate = PromptEditor.Text ?? string.Empty;

            if (LocalModelPicker.SelectedItem is LocalTextAnalysisModel model)
            {
                _options.LocalModelId = model.Id;
            }

            _analysisService.UpdateOptions(_options);

            if (SaveStatusLabel != null)
            {
                SaveStatusLabel.Text = "Настройки сохранены.";
                SaveStatusLabel.TextColor = Colors.Gray;
            }
        }

        private async void OnCloseClicked(object? sender, EventArgs e)
        {
            try
            {
                if (Navigation?.ModalStack?.Count > 0)
                {
                    await Navigation.PopModalAsync();
                }
                else if (Navigation?.NavigationStack?.Count > 1)
                {
                    await Navigation.PopAsync();
                }
                else if (Shell.Current != null)
                {
                    await Shell.Current.GoToAsync("..");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            }
        }
    }
}
