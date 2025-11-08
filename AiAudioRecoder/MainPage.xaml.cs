using Microsoft.Maui.Controls;
using Microsoft.Maui.Essentials;
using System;
using System.Threading.Tasks;

namespace AiAudioRecoder;

public partial class MainPage : ContentPage
{
    private bool _isRecording = false;

    public MainPage()
    {
        InitializeComponent();

        // Set default model selection
        if (ModelPicker.ItemsSource != null && ModelPicker.SelectedIndex == -1)
        {
            ModelPicker.SelectedIndex = 1; // Select "base" model by default
        }
    }

    private async void OnDownloadModelClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(ModelPicker.SelectedItem?.ToString()))
        {
            await DisplayAlertAsync("Ошибка", "Выберите модель для скачивания", "OK");
            return;
        }

        DownloadButton.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        DownloadProgress.Progress = 0;

        try
        {
            // Simulate model download with progress
            for (int i = 0; i <= 100; i += 10)
            {
                DownloadProgress.Progress = i / 100.0;
                await Task.Delay(300); // Simulate download time
            }

            await DisplayAlertAsync("Успех", $"Модель '{ModelPicker.SelectedItem}' успешно скачана!", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Не удалось скачать модель: {ex.Message}", "OK");
        }
        finally
        {
            DownloadProgress.IsVisible = false;
            DownloadButton.IsEnabled = true;
        }
    }

    private async void OnRecordClicked(object sender, EventArgs e)
    {
        if (_isRecording)
        {
            await DisplayAlertAsync("Информация", "Запись уже идет", "OK");
            return;
        }

        try
        {
            // Check microphone permissions
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            }

            if (status == PermissionStatus.Granted)
            {
                // Navigate to RecordPage for actual recording
                await Shell.Current.GoToAsync("//records");
            }
            else
            {
                await DisplayAlertAsync("Ошибка", "Для записи требуется разрешение на использование микрофона", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Не удалось проверить разрешения: {ex.Message}", "OK");
        }
    }

    private async void OnShowRecordsClicked(object sender, EventArgs e)
    {
        try
        {
            // Navigate to RecordPage
            await Shell.Current.GoToAsync("//records");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Не удалось открыть записи: {ex.Message}", "OK");
        }
    }

    private void OnAudioSourceToggled(object sender, ToggledEventArgs e)
    {
        if (sender is Switch switchControl)
        {
            // Find the label that describes the audio source
            var parentLayout = switchControl.Parent as Microsoft.Maui.Controls.Layout;
            if (parentLayout != null)
            {
                var children = parentLayout.Children;
                var switchIndex = children.IndexOf(switchControl);
                if (switchIndex >= 0 && switchIndex + 1 < children.Count)
                {
                    if (children[switchIndex + 1] is Label descriptionLabel)
                    {
                        descriptionLabel.Text = e.Value 
                            ? "Микрофон (выкл.) / Система (вкл.)" 
                            : "Микрофон (вкл.) / Система (выкл.)";
                    }
                }
            }
        }
    }
}
