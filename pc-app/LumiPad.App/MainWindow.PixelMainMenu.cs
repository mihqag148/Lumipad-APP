using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IO = System.IO;

namespace LumiPad.App;

public partial class MainWindow
{
    private void SetPixelHomePreviewMode(bool mainMenu)
    {
        if (!IsPixelProActive)
            return;

        if (PixelHomeScreensaverPreviewPanel is not null)
            PixelHomeScreensaverPreviewPanel.Visibility =
                mainMenu
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (PixelHomeMenuPreviewPanel is not null)
            PixelHomeMenuPreviewPanel.Visibility =
                mainMenu
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (PixelHomePreviewModeText is not null)
            PixelHomePreviewModeText.Text =
                mainMenu
                    ? "MAIN MENU"
                    : "SCREENSAVER";
    }

    private void ScreensaverEditor_PreviewMouseDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e) =>
        SetPixelHomePreviewMode(false);

    private void MainMenuEditor_PreviewMouseDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e) =>
        SetPixelHomePreviewMode(true);

    private void BuildPixelMainMenuEditor()
    {
        if (PixelMenuSlotsEditor is null ||
            PixelMenuPreviewSlots is null ||
            _pixelMenuActionCombos.Count == 12)
        {
            return;
        }

        PixelMenuSlotsEditor.Children.Clear();
        PixelMenuPreviewSlots.Children.Clear();
        _pixelMenuActionCombos.Clear();
        _pixelMenuEditorIcons.Clear();
        _pixelMenuPreviewIcons.Clear();
        _pixelMenuPreviewLabels.Clear();

        for (int slot = 0; slot < 12; slot++)
        {
            int capturedSlot = slot;

            var editorIcon =
                new System.Windows.Controls.Image
                {
                    Width = 42,
                    Height = 42,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 5)
                };

            var combo =
                new System.Windows.Controls.ComboBox
                {
                    Tag = capturedSlot,
                    MinWidth = 118,
                    Margin = new Thickness(0, 0, 0, 5)
                };

            combo.SelectionChanged +=
                PixelMenuSlotAction_SelectionChanged;

            var chooseButton =
                new System.Windows.Controls.Button
                {
                    Content = "Icon",
                    Tag = capturedSlot,
                    Padding = new Thickness(7, 4, 7, 4),
                    Margin = new Thickness(0, 0, 4, 0)
                };

            chooseButton.Click +=
                PixelMenuChooseIcon_Click;

            var clearButton =
                new System.Windows.Controls.Button
                {
                    Content = "×",
                    Tag = capturedSlot,
                    Width = 30,
                    Padding = new Thickness(0)
                };

            clearButton.Click +=
                PixelMenuClearIcon_Click;

            var buttons =
                new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };

            buttons.Children.Add(chooseButton);
            buttons.Children.Add(clearButton);

            var editorStack =
                new StackPanel();

            editorStack.Children.Add(
                new TextBlock
                {
                    Text = $"Slot {slot + 1}",
                    FontSize = 10,
                    Foreground =
                        TryFindResource("Muted") as System.Windows.Media.Brush,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2)
                });

            editorStack.Children.Add(editorIcon);
            editorStack.Children.Add(combo);
            editorStack.Children.Add(buttons);

            var editorBorder =
                new Border
                {
                    Child = editorStack,
                    Margin = new Thickness(3),
                    Padding = new Thickness(6),
                    CornerRadius = new CornerRadius(9),
                    BorderBrush =
                        TryFindResource("Line") as System.Windows.Media.Brush,
                    BorderThickness = new Thickness(1),
                    Background =
                        TryFindResource("ControlBg") as System.Windows.Media.Brush
                };

            PixelMenuSlotsEditor.Children.Add(
                editorBorder);

            var previewIcon =
                new System.Windows.Controls.Image
                {
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                    Margin = new Thickness(9)
                };

            var previewLabel =
                new TextBlock
                {
                    Text = "--",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.White,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 92,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(7)
                };

            var previewGrid =
                new Grid();

            previewGrid.Children.Add(
                previewIcon);

            previewGrid.Children.Add(
                previewLabel);

            PixelMenuPreviewSlots.Children.Add(
                new Border
                {
                    Child = previewGrid,
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(9),
                    BorderBrush =
                        new SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(
                                150,
                                180,
                                180,
                                184)),
                    BorderThickness = new Thickness(1),
                    Background =
                        new SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(
                                35,
                                0,
                                0,
                                0))
                });

            _pixelMenuActionCombos.Add(combo);
            _pixelMenuEditorIcons.Add(editorIcon);
            _pixelMenuPreviewIcons.Add(previewIcon);
            _pixelMenuPreviewLabels.Add(previewLabel);
        }
    }

    private void RefreshPixelMainMenuActionChoices()
    {
        if (_pixelMenuActionCombos.Count != 12)
            return;

        _syncingPixelMenuUi = true;

        try
        {
            PixelProMainMenuPage page =
                _pixelMainMenu.Pages[
                    Math.Clamp(
                        _pixelMenuPageIndex,
                        0,
                        3)];

            for (int slot = 0; slot < 12; slot++)
            {
                System.Windows.Controls.ComboBox combo =
                    _pixelMenuActionCombos[slot];

                combo.Items.Clear();

                combo.Items.Add(
                    new ComboBoxItem
                    {
                        Content = "None",
                        Tag = 0
                    });

                foreach (ActionScriptDefinition action in
                         _actionScripts
                             .Where(x => x.ActionId is >= 1 and <= 32)
                             .OrderBy(x => x.ActionId))
                {
                    combo.Items.Add(
                        new ComboBoxItem
                        {
                            Content =
                                $"A{action.ActionId:00} · {action.Name}",
                            Tag = action.ActionId
                        });
                }

                int desired =
                    page.Slots[slot].ActionId;

                ComboBoxItem? selected =
                    combo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(
                            item =>
                                Convert.ToInt32(
                                    item.Tag ?? 0) ==
                                desired);

                combo.SelectedItem =
                    selected ??
                    combo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault();
            }
        }
        finally
        {
            _syncingPixelMenuUi = false;
        }
    }

    private void RefreshPixelMainMenuUi()
    {
        if (PixelMenuPageCombo is null ||
            PixelMenuLayerCombo is null)
        {
            return;
        }

        BuildPixelMainMenuEditor();

        int pageIndex =
            Math.Clamp(
                _pixelMenuPageIndex,
                0,
                3);

        PixelProMainMenuPage page =
            _pixelMainMenu.Pages[pageIndex];

        _syncingPixelMenuUi = true;

        try
        {
            SelectComboTag(
                PixelMenuPageCombo,
                pageIndex.ToString());

            SelectComboTag(
                PixelMenuLayerCombo,
                page.Layer.ToString());

            PixelMenuBrightnessSlider.Value =
                _pixelMainMenu.BrightnessPercent;

            PixelMenuBrightnessText.Text =
                $"{_pixelMainMenu.BrightnessPercent}%";

            PixelMenuOpacitySlider.Value =
                _pixelMainMenu.OpacityPercent;

            PixelMenuOpacityText.Text =
                $"{_pixelMainMenu.OpacityPercent}%";

            PixelMenuBrightnessOverlay.Opacity =
                1.0 -
                _pixelMainMenu.BrightnessPercent /
                100.0;

            PixelMenuBackgroundPreview.Opacity =
                _pixelMainMenu.OpacityPercent /
                100.0;

            PixelMenuPreviewLayerText.Text =
                $"Menu {pageIndex + 1} · Layer {page.Layer}";

            if (PixelMenuEditorLayerText is not null)
                PixelMenuEditorLayerText.Text =
                    $"Menu {pageIndex + 1} · Layer {page.Layer}";

            string? background =
                _pixelMainMenu.BackgroundPath;

            PixelMenuBackgroundName.Text =
                string.IsNullOrWhiteSpace(background)
                    ? L("No background", "Chưa có ảnh nền")
                    : IO.Path.GetFileName(background);

            PixelMenuBackgroundPreview.Source =
                LoadLocalImageSource(background);

            for (int slot = 0; slot < 12; slot++)
            {
                string? icon =
                    page.Slots[slot].IconPath;

                ImageSource? source =
                    LoadLocalImageSource(icon);

                _pixelMenuEditorIcons[slot].Source =
                    source;

                _pixelMenuPreviewIcons[slot].Source =
                    source;

                int actionId =
                    page.Slots[slot].ActionId;

                ActionScriptDefinition? action =
                    _actionScripts.FirstOrDefault(
                        x => x.ActionId == actionId);

                bool hasIcon =
                    source is not null;

                _pixelMenuPreviewIcons[slot].Visibility =
                    hasIcon
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                _pixelMenuPreviewLabels[slot].Visibility =
                    hasIcon
                        ? Visibility.Collapsed
                        : Visibility.Visible;

                _pixelMenuPreviewLabels[slot].Text =
                    actionId <= 0
                        ? ""
                        : action is null
                            ? $"A{actionId:00}"
                            : action.Name;
            }
        }
        finally
        {
            _syncingPixelMenuUi = false;
        }

        RefreshPixelMainMenuActionChoices();
    }

    private static ImageSource? LoadLocalImageSource(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes =
                IO.File.ReadAllBytes(path);

            using var stream =
                new IO.MemoryStream(bytes);

            var bitmap =
                new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption =
                BitmapCacheOption.OnLoad;
            bitmap.StreamSource =
                stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void PixelMenuPageCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            PixelMenuPageCombo.SelectedValue is not string text ||
            !int.TryParse(text, out int page))
        {
            return;
        }

        _pixelMenuPageIndex =
            Math.Clamp(page, 0, 3);

        SetPixelHomePreviewMode(true);
        RefreshPixelMainMenuUi();
    }

    private void PixelMenuLayerCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            PixelMenuLayerCombo.SelectedValue is not string text ||
            !int.TryParse(text, out int layer))
        {
            return;
        }

        PixelProMainMenuPage page =
            _pixelMainMenu.Pages[
                Math.Clamp(
                    _pixelMenuPageIndex,
                    0,
                    3)];

        page.Layer =
            Math.Clamp(layer, 0, 3);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        RefreshPixelMainMenuUi();
    }

    private void PixelMenuSlotAction_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            sender is not System.Windows.Controls.ComboBox combo ||
            combo.Tag is not int slot ||
            combo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        int actionId =
            Convert.ToInt32(
                item.Tag ?? 0);

        _pixelMainMenu
            .Pages[Math.Clamp(_pixelMenuPageIndex, 0, 3)]
            .Slots[Math.Clamp(slot, 0, 11)]
            .ActionId =
                Math.Clamp(actionId, 0, 32);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        RefreshPixelMainMenuUi();
    }

    private void PixelMenuChooseBackground_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog =
            new Microsoft.Win32.OpenFileDialog
            {
                Title =
                    L(
                        "Choose PIXEL PRO main-menu background",
                        "Chọn ảnh nền Main Menu PIXEL PRO"),
                Filter =
                    "Static image|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true,
                Multiselect = false
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            PixelProMainMenuMediaService
                .ValidateStaticImage(
                    dialog.FileName);

            _pixelMainMenu.BackgroundPath =
                dialog.FileName;

            SetPixelHomePreviewMode(true);

            PixelProMainMenuStore.Save(
                _pixelMainMenu);

            RefreshPixelMainMenuUi();
        }
        catch (Exception ex)
        {
            PixelMenuStatusText.Text =
                L(
                    $"Background error: {ex.Message}",
                    $"Lỗi ảnh nền: {ex.Message}");
        }
    }

    private void PixelMenuClearBackground_Click(
        object sender,
        RoutedEventArgs e)
    {
        _pixelMainMenu.BackgroundPath =
            null;

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        RefreshPixelMainMenuUi();
    }

    private void PixelMenuBrightnessSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi)
        {
            return;
        }

        _pixelMainMenu.BrightnessPercent =
            Math.Clamp(
                (int)Math.Round(e.NewValue),
                20,
                100);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        if (PixelMenuBrightnessText is not null)
            PixelMenuBrightnessText.Text =
                $"{_pixelMainMenu.BrightnessPercent}%";

        if (PixelMenuBrightnessOverlay is not null)
            PixelMenuBrightnessOverlay.Opacity =
                1.0 -
                _pixelMainMenu.BrightnessPercent /
                100.0;
    }

    private void PixelMenuOpacitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi)
        {
            return;
        }

        _pixelMainMenu.OpacityPercent =
            Math.Clamp(
                (int)Math.Round(e.NewValue),
                0,
                100);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        if (PixelMenuOpacityText is not null)
            PixelMenuOpacityText.Text =
                $"{_pixelMainMenu.OpacityPercent}%";

        if (PixelMenuBackgroundPreview is not null)
            PixelMenuBackgroundPreview.Opacity =
                _pixelMainMenu.OpacityPercent /
                100.0;
    }

    private void PixelMenuChooseIcon_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not int slot)
        {
            return;
        }

        var dialog =
            new Microsoft.Win32.OpenFileDialog
            {
                Title =
                    L(
                        $"Choose icon for slot {slot + 1}",
                        $"Chọn icon cho ô {slot + 1}"),
                Filter =
                    "Static image|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true,
                Multiselect = false
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            PixelProMainMenuMediaService
                .ValidateStaticImage(
                    dialog.FileName);

            _pixelMainMenu
                .Pages[Math.Clamp(_pixelMenuPageIndex, 0, 3)]
                .Slots[Math.Clamp(slot, 0, 11)]
                .IconPath =
                    dialog.FileName;

            SetPixelHomePreviewMode(true);

            PixelProMainMenuStore.Save(
                _pixelMainMenu);

            RefreshPixelMainMenuUi();
        }
        catch (Exception ex)
        {
            PixelMenuStatusText.Text =
                L(
                    $"Icon error: {ex.Message}",
                    $"Lỗi icon: {ex.Message}");
        }
    }

    private void PixelMenuClearIcon_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not int slot)
        {
            return;
        }

        _pixelMainMenu
            .Pages[Math.Clamp(_pixelMenuPageIndex, 0, 3)]
            .Slots[Math.Clamp(slot, 0, 11)]
            .IconPath =
                null;

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        RefreshPixelMainMenuUi();
    }

    private async void PixelMenuSave_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsPixelProActive ||
            _serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
        {
            PixelMenuStatusText.Text =
                L(
                    "Connect PIXEL PRO by USB first.",
                    "Hãy kết nối PIXEL PRO bằng USB trước.");
            return;
        }

        SetPixelHomePreviewMode(true);

        PixelMenuSaveButton.IsEnabled = false;
        PixelMenuUploadProgress.Value = 0;

        try
        {
            PixelMenuStatusText.Text =
                L(
                    "Preparing main-menu artwork…",
                    "Đang xử lý ảnh Main Menu…");

            const int TotalOperations =
                1 + 4 + (4 * 12) + 1;

            int completed = 0;

            void ReportOperation()
            {
                completed++;
                PixelMenuUploadProgress.Value =
                    Math.Clamp(
                        completed * 100.0 /
                        TotalOperations,
                        0,
                        100);
            }

            if (!string.IsNullOrWhiteSpace(
                    _pixelMainMenu.BackgroundPath))
            {
                byte[] background =
                    await Task.Run(
                        () =>
                            PixelProMainMenuMediaService
                                .CreateBackgroundJpeg(
                                    _pixelMainMenu.BackgroundPath!,
                                    _pixelMainMenu.BrightnessPercent,
                                    _pixelMainMenu.OpacityPercent));

                if (!await pixel.UploadMainMenuBackgroundAsync(
                        background))
                {
                    throw new InvalidOperationException(
                        "PIXEL PRO rejected the main-menu background.");
                }
            }
            else if (!await pixel.ClearMainMenuBackgroundAsync())
            {
                throw new InvalidOperationException(
                    "PIXEL PRO could not clear the main-menu background.");
            }

            ReportOperation();

            var iconCache =
                new Dictionary<string, byte[]>(
                    StringComparer.OrdinalIgnoreCase);

            for (int page = 0; page < 4; page++)
            {
                PixelProMainMenuPage menu =
                    _pixelMainMenu.Pages[page];

                string[] labels =
                    menu.Slots
                        .Select(slot =>
                        {
                            ActionScriptDefinition? action =
                                _actionScripts.FirstOrDefault(
                                    item =>
                                        item.ActionId ==
                                        slot.ActionId);

                            return action?.Name ??
                                   (slot.ActionId > 0
                                       ? $"A{slot.ActionId:00}"
                                       : "");
                        })
                        .ToArray();

                if (!await pixel.SetMainMenuPageAsync(
                        page,
                        menu.Layer,
                        menu.Slots.Select(x => x.ActionId).ToArray(),
                        labels))
                {
                    throw new InvalidOperationException(
                        $"PIXEL PRO rejected Menu {page + 1} config.");
                }

                ReportOperation();

                for (int slot = 0; slot < 12; slot++)
                {
                    string? iconPath =
                        menu.Slots[slot].IconPath;

                    if (!string.IsNullOrWhiteSpace(iconPath) &&
                        IO.File.Exists(iconPath))
                    {
                        if (!iconCache.TryGetValue(
                                iconPath,
                                out byte[]? iconBytes))
                        {
                            iconBytes =
                                await Task.Run(
                                    () =>
                                        PixelProMainMenuMediaService
                                            .CreateIconRgb565(
                                                iconPath));

                            iconCache[iconPath] =
                                iconBytes;
                        }

                        if (!await pixel.UploadMainMenuIconAsync(
                                page,
                                slot,
                                iconBytes))
                        {
                            throw new InvalidOperationException(
                                $"PIXEL PRO rejected icon Menu {page + 1} / Slot {slot + 1}.");
                        }
                    }
                    else if (!await pixel.ClearMainMenuIconAsync(
                                 page,
                                 slot))
                    {
                        throw new InvalidOperationException(
                            $"PIXEL PRO could not clear icon Menu {page + 1} / Slot {slot + 1}.");
                    }

                    ReportOperation();
                }
            }

            if (!await pixel.ShowMainMenuAsync())
            {
                throw new InvalidOperationException(
                    "PIXEL PRO did not show the main menu.");
            }

            ReportOperation();

            PixelProMainMenuStore.Save(
                _pixelMainMenu);

            PixelMenuUploadProgress.Value = 100;
            PixelMenuStatusText.Text =
                L(
                    "Main menu saved to PIXEL PRO flash.",
                    "Đã lưu Main Menu vào flash PIXEL PRO.");

            await UpdateMemoryUsageAsync();
        }
        catch (Exception ex)
        {
            PixelMenuStatusText.Text =
                L(
                    $"Main-menu upload failed: {ex.Message}",
                    $"Tải Main Menu lỗi: {ex.Message}");

            AddLog(
                "ERROR",
                "PIXEL MENU",
                ex.ToString());
        }
        finally
        {
            PixelMenuSaveButton.IsEnabled =
                true;
        }
    }

    private async void PixelMenuShowNow_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
        {
            PixelMenuStatusText.Text =
                L(
                    "Connect PIXEL PRO first.",
                    "Hãy kết nối PIXEL PRO trước.");
            return;
        }

        SetPixelHomePreviewMode(true);

        bool ok =
            await pixel.ShowMainMenuAsync();

        PixelMenuStatusText.Text =
            ok
                ? L(
                    "Showing PIXEL PRO main menu.",
                    "Đang hiển thị Main Menu PIXEL PRO.")
                : L(
                    "PIXEL PRO did not confirm the main menu.",
                    "PIXEL PRO chưa xác nhận Main Menu.");
    }
}
