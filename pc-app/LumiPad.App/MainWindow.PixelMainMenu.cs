using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using IO = System.IO;

namespace LumiPad.App;

public partial class MainWindow
{
    private int PixelMenuProfileIndex =>
        Math.Clamp(
            _pixelSelectedProfile,
            0,
            PixelProMainMenuStore.ProfileCount - 1);

    private PixelProMainMenuProfile PixelMenuProfile =>
        _pixelMainMenu.Profiles[PixelMenuProfileIndex];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

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
            _pixelMenuActionCombos.Count == PixelProMainMenuStore.SlotCount)
        {
            return;
        }

        PixelMenuSlotsEditor.Children.Clear();
        PixelMenuPreviewSlots.Children.Clear();
        _pixelMenuActionCombos.Clear();
        _pixelMenuEditorIcons.Clear();
        _pixelMenuPreviewIcons.Clear();
        _pixelMenuPreviewLabels.Clear();

        for (int slot = 0; slot < PixelProMainMenuStore.SlotCount; slot++)
        {
            int capturedSlot = slot;

            var editorIcon =
                new System.Windows.Controls.Image
                {
                    Width = 26,
                    Height = 26,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment =
                        System.Windows.VerticalAlignment.Center
                };

            var combo =
                new System.Windows.Controls.ComboBox
                {
                    Tag = capturedSlot,
                    MinWidth = 66,
                    FontSize = 9,
                    Height = 22,
                    Margin = new Thickness(4, 0, 0, 0)
                };

            combo.SelectionChanged +=
                PixelMenuSlotAction_SelectionChanged;

            var appButton =
                new System.Windows.Controls.Button
                {
                    Content = "APP",
                    Tag = capturedSlot,
                    Width = 30,
                    Height = 20,
                    FontSize = 7,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = "Choose an app and use its icon"
                };

            appButton.Click +=
                PixelMenuChooseApp_Click;

            var chooseButton =
                new System.Windows.Controls.Button
                {
                    Content = "IMG",
                    Tag = capturedSlot,
                    Width = 30,
                    Height = 20,
                    FontSize = 7,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = "Choose a custom icon"
                };

            chooseButton.Click +=
                PixelMenuChooseIcon_Click;

            var clearButton =
                new System.Windows.Controls.Button
                {
                    Content = "×",
                    Tag = capturedSlot,
                    Width = 20,
                    Height = 20,
                    FontSize = 10,
                    Padding = new Thickness(0),
                    ToolTip = "Clear icon"
                };

            clearButton.Click +=
                PixelMenuClearIcon_Click;

            var buttons =
                new StackPanel
                {
                    Orientation =
                        System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Right,
                    Margin = new Thickness(0, 3, 0, 0)
                };

            buttons.Children.Add(appButton);
            buttons.Children.Add(chooseButton);
            buttons.Children.Add(clearButton);

            var slotText =
                new TextBlock
                {
                    Text = $"{slot + 1}",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground =
                        TryFindResource("Muted") as
                        System.Windows.Media.Brush,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment =
                        System.Windows.VerticalAlignment.Center
                };

            var editorGrid =
                new Grid();

            editorGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            editorGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            editorGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(16)
                });

            editorGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(30)
                });

            editorGrid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(
                        1,
                        GridUnitType.Star)
                });

            Grid.SetRow(slotText, 0);
            Grid.SetColumn(slotText, 0);

            Grid.SetRow(editorIcon, 0);
            Grid.SetColumn(editorIcon, 1);

            Grid.SetRow(combo, 0);
            Grid.SetColumn(combo, 2);

            Grid.SetRow(buttons, 1);
            Grid.SetColumn(buttons, 0);
            Grid.SetColumnSpan(buttons, 3);

            editorGrid.Children.Add(slotText);
            editorGrid.Children.Add(editorIcon);
            editorGrid.Children.Add(combo);
            editorGrid.Children.Add(buttons);

            PixelMenuSlotsEditor.Children.Add(
                new Border
                {
                    Child = editorGrid,
                    Margin = new Thickness(1),
                    Padding = new Thickness(4, 3, 4, 3),
                    CornerRadius = new CornerRadius(7),
                    BorderBrush =
                        TryFindResource("Line") as
                        System.Windows.Media.Brush,
                    BorderThickness = new Thickness(1),
                    Background =
                        TryFindResource("ControlBg") as
                        System.Windows.Media.Brush
                });

            var previewIcon =
                new System.Windows.Controls.Image
                {
                    // Firmware renders menu assets at 96x96 on the native
                    // 480x320 canvas. With the 360x240 preview this remains
                    // ~72px on screen, matching the previous apparent size.
                    Width = 96,
                    Height = 96,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment =
                        System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                    SnapsToDevicePixels = true
                };

            RenderOptions.SetBitmapScalingMode(
                previewIcon,
                BitmapScalingMode.HighQuality);

            var previewLabel =
                new TextBlock
                {
                    Text = "",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground =
                        System.Windows.Media.Brushes.White,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 104,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment =
                        System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0)
                };

            // Keep the icon centered at one consistent scale. The action name
            // is a fallback only when the slot has no custom icon.
            var previewGrid =
                new Grid
                {
                    Margin = new Thickness(2, 1, 2, 1)
                };

            previewGrid.Children.Add(previewIcon);
            previewGrid.Children.Add(previewLabel);

            PixelMenuPreviewSlots.Children.Add(
                previewGrid);

            _pixelMenuActionCombos.Add(combo);
            _pixelMenuEditorIcons.Add(editorIcon);
            _pixelMenuPreviewIcons.Add(previewIcon);
            _pixelMenuPreviewLabels.Add(previewLabel);
        }
    }

    private void RefreshPixelMainMenuActionChoices()
    {
        if (_pixelMenuActionCombos.Count != PixelProMainMenuStore.SlotCount ||
            _pixelMainMenu.Profiles.Length <
                PixelProMainMenuStore.ProfileCount)
        {
            return;
        }

        _syncingPixelMenuUi = true;

        try
        {
            PixelProMainMenuProfile profile =
                PixelMenuProfile;

            for (int slot = 0; slot < PixelProMainMenuStore.SlotCount; slot++)
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
                             .Where(
                                 x =>
                                     x.ActionId is
                                         >= 1 and <= 32)
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
                    profile.Slots[slot].ActionId;

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

    private void RefreshPixelMenuProfileChoices()
    {
        if (PixelMenuProfileCombo is null)
            return;

        _pixelProfileCatalog.Normalize();

        PixelMenuProfileCombo.Items.Clear();

        for (int profile = 0;
             profile < PixelProMainMenuStore.ProfileCount;
             profile++)
        {
            string name =
                profile < _pixelProfileCatalog.Count
                    ? _pixelProfileCatalog.Names[profile]
                    : $"Profile {profile + 1}";

            PixelMenuProfileCombo.Items.Add(
                new ComboBoxItem
                {
                    Content =
                        $"{profile + 1:00} · {name}",
                    Tag = profile
                });
        }

        PixelMenuProfileCombo.SelectedIndex =
            PixelMenuProfileIndex;
    }

    private void RefreshPixelMainMenuUi()
    {
        if (PixelMenuProfileCombo is null ||
            PixelMenuBlurSlider is null ||
            PixelMenuScaleCombo is null ||
            _pixelMainMenu.Profiles.Length <
                PixelProMainMenuStore.ProfileCount)
        {
            return;
        }

        BuildPixelMainMenuEditor();

        int profileIndex =
            PixelMenuProfileIndex;

        PixelProMainMenuProfile profile =
            _pixelMainMenu.Profiles[profileIndex];

        _syncingPixelMenuUi = true;

        try
        {
            RefreshPixelMenuProfileChoices();

            PixelMenuBlurSlider.Value =
                profile.BlurPercent;

            PixelMenuBlurText.Text =
                $"{profile.BlurPercent}%";

            PixelMenuOpacitySlider.Value =
                profile.OpacityPercent;

            PixelMenuOpacityText.Text =
                $"{profile.OpacityPercent}%";

            PixelMenuScaleCombo.SelectedValue =
                profile.ScaleMode.ToString();

            if (PixelMenuEditorLayerText is not null)
            {
                PixelMenuEditorLayerText.Text =
                    $"Profile {profileIndex + 1:00}";
            }

            string? background =
                profile.BackgroundPath;

            PixelMenuBackgroundName.Text =
                string.IsNullOrWhiteSpace(background)
                    ? L(
                        "No background",
                        "Chưa có ảnh nền")
                    : IO.Path.GetFileName(background);

            PixelMenuBackgroundPreview.Source =
                LoadPixelMenuBackgroundPreview(
                    profile);

            PixelMenuBackgroundPreview.Opacity =
                1.0;

            PixelMenuBackgroundPreview.Stretch =
                Stretch.Fill;

            for (int slot = 0; slot < PixelProMainMenuStore.SlotCount; slot++)
            {
                string? icon =
                    profile.Slots[slot].IconPath;

                ImageSource? source =
                    LoadPixelMenuIconPreview(icon);

                _pixelMenuEditorIcons[slot].Source =
                    source;

                _pixelMenuPreviewIcons[slot].Source =
                    source;

                int actionId =
                    profile.Slots[slot].ActionId;

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
                    !hasIcon &&
                    actionId > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;

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
        RefreshPixelMenuStatusPreview();
    }

    private void RefreshPixelMenuStatusPreview()
    {
        if (PixelMenuDockIcon1 is null ||
            PixelMenuDockIcon2 is null ||
            PixelMenuDockIcon3 is null ||
            PixelMenuDockIcon4 is null)
        {
            return;
        }

        int hostOs =
            RuntimeInformation.IsOSPlatform(
                OSPlatform.OSX)
                ? 2
                : RuntimeInformation.IsOSPlatform(
                    OSPlatform.Linux)
                    ? 3
                    : 1;

        System.Windows.Controls.Image[] icons =
        [
            PixelMenuDockIcon1,
            PixelMenuDockIcon2,
            PixelMenuDockIcon3,
            PixelMenuDockIcon4
        ];

        for (int slot = 0;
             slot < icons.Length;
             slot++)
        {
            icons[slot].Source =
                LoadImageSource(
                    PixelProMainMenuMediaService
                        .CreateDockIconPreviewPng(
                            hostOs,
                            slot));

            RenderOptions.SetBitmapScalingMode(
                icons[slot],
                BitmapScalingMode.HighQuality);
        }
    }

    private static ImageSource? LoadImageSource(
        byte[] bytes)
    {
        try
        {
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
            return LoadImageSource(
                IO.File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadPixelMenuIconPreview(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            return LoadImageSource(
                PixelProMainMenuMediaService
                    .CreateIconPreviewPng(
                        path));
        }
        catch
        {
            return LoadLocalImageSource(
                path);
        }
    }

    private static ImageSource? LoadPixelMenuBackgroundPreview(
        PixelProMainMenuProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BackgroundPath) ||
            !IO.File.Exists(profile.BackgroundPath))
        {
            return null;
        }

        try
        {
            byte[] jpeg =
                PixelProMainMenuMediaService
                    .CreateBackgroundJpeg(
                        profile.BackgroundPath,
                        profile.BlurPercent,
                        profile.OpacityPercent,
                        profile.ScaleMode);

            return LoadImageSource(jpeg);
        }
        catch
        {
            return LoadLocalImageSource(
                profile.BackgroundPath);
        }
    }

    private void PixelMenuProfileCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            PixelMenuProfileCombo?.SelectedItem is not
                ComboBoxItem item)
        {
            return;
        }

        int profile =
            Math.Clamp(
                Convert.ToInt32(item.Tag ?? 0),
                0,
                PixelProMainMenuStore.ProfileCount - 1);

        _pixelSelectedProfile =
            profile;

        SetPixelHomePreviewMode(true);

        if (PixelProfileCombo is not null &&
            PixelProfileCombo.SelectedIndex != profile)
        {
            PixelProfileCombo.SelectedIndex =
                profile;
        }
        else if (_serial is PixelProCdcLink pixel &&
                 pixel.IsConnected)
        {
            pixel.SetProfileLayer(
                profile,
                _pixelSelectedLayer);
        }

        RefreshPixelMainMenuUi();
    }

    private static string? PixelMenuActionRunPath(
        ActionScriptDefinition? action)
    {
        if (action?.Steps is null)
            return null;

        ActionScriptStep? run =
            action.Steps.FirstOrDefault(
                step =>
                    step.Type.Equals(
                        "Run",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(step.Value) &&
                    IO.File.Exists(step.Value));

        return run?.Value;
    }

    private void PixelMenuSlotAction_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            sender is not
                System.Windows.Controls.ComboBox combo ||
            combo.Tag is not int slot ||
            combo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        slot =
            Math.Clamp(
                slot,
                0,
                PixelProMainMenuStore.SlotCount - 1);

        PixelProMainMenuSlot target =
            PixelMenuProfile.Slots[slot];

        int actionId =
            Math.Clamp(
                Convert.ToInt32(
                    item.Tag ?? 0),
                0,
                32);

        target.ActionId =
            actionId;

        ActionScriptDefinition? action =
            _actionScripts.FirstOrDefault(
                candidate =>
                    candidate.ActionId ==
                    actionId);

        string? appPath =
            PixelMenuActionRunPath(action);

        // Changing the dropdown changes the entire visual assignment too.
        // This prevents a previous Chrome icon remaining on Bambu Studio, etc.
        target.AppPath =
            appPath;

        if (!string.IsNullOrWhiteSpace(appPath))
        {
            string? extractedIcon =
                ExtractPixelMenuAppIcon(
                    appPath);

            target.IconPath =
                extractedIcon;

            target.AutoIcon =
                !string.IsNullOrWhiteSpace(
                    extractedIcon);
        }
        else
        {
            target.IconPath =
                null;
            target.AutoIcon =
                false;
        }

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

            PixelMenuProfile.BackgroundPath =
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
        PixelMenuProfile.BackgroundPath =
            null;

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        RefreshPixelMainMenuUi();
    }

    private void PixelMenuBlurSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi)
        {
            return;
        }

        PixelMenuProfile.BlurPercent =
            Math.Clamp(
                (int)Math.Round(e.NewValue),
                0,
                100);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        if (PixelMenuBlurText is not null)
        {
            PixelMenuBlurText.Text =
                $"{PixelMenuProfile.BlurPercent}%";
        }

        if (PixelMenuBackgroundPreview is not null)
        {
            PixelMenuBackgroundPreview.Source =
                LoadPixelMenuBackgroundPreview(
                    PixelMenuProfile);
        }
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

        PixelMenuProfile.OpacityPercent =
            Math.Clamp(
                (int)Math.Round(e.NewValue),
                0,
                100);

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        if (PixelMenuOpacityText is not null)
        {
            PixelMenuOpacityText.Text =
                $"{PixelMenuProfile.OpacityPercent}%";
        }

        if (PixelMenuBackgroundPreview is not null)
        {
            PixelMenuBackgroundPreview.Source =
                LoadPixelMenuBackgroundPreview(
                    PixelMenuProfile);
        }
    }

    private void PixelMenuScaleCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            _syncingPixelMenuUi ||
            PixelMenuScaleCombo?.SelectedValue is not
                string value ||
            !Enum.TryParse(
                value,
                true,
                out ScreensaverScaleMode scaleMode))
        {
            return;
        }

        if (scaleMode is not (
            ScreensaverScaleMode.Fill or
            ScreensaverScaleMode.Fit or
            ScreensaverScaleMode.Stretch))
        {
            scaleMode =
                ScreensaverScaleMode.Fill;
        }

        PixelMenuProfile.ScaleMode =
            scaleMode;

        SetPixelHomePreviewMode(true);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        if (PixelMenuBackgroundPreview is not null)
        {
            PixelMenuBackgroundPreview.Source =
                LoadPixelMenuBackgroundPreview(
                    PixelMenuProfile);
        }
    }

    private static string? ExtractPixelMenuAppIcon(
        string appPath)
    {
        if (!IO.File.Exists(appPath))
            return null;

        string extension =
            IO.Path.GetExtension(appPath);

        try
        {
            string folder =
                IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "LumiPad",
                    "pixel_menu_icons");

            IO.Directory.CreateDirectory(folder);

            string hash =
                Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            appPath.ToUpperInvariant())))
                    .Substring(0, 16);

            string cachePath =
                IO.Path.Combine(
                    folder,
                    $"{hash}.png");

            if (string.Equals(
                    extension,
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                IntPtr[] handles =
                    new IntPtr[1];

                uint[] ids =
                    new uint[1];

                uint count =
                    PrivateExtractIcons(
                        appPath,
                        0,
                        256,
                        256,
                        handles,
                        ids,
                        1,
                        0);

                if (count > 0 &&
                    handles[0] != IntPtr.Zero)
                {
                    try
                    {
                        using Drawing.Icon borrowed =
                            Drawing.Icon.FromHandle(
                                handles[0]);

                        using Drawing.Bitmap bitmap =
                            borrowed.ToBitmap();

                        bitmap.Save(
                            cachePath,
                            DrawingImaging.ImageFormat.Png);

                        return cachePath;
                    }
                    finally
                    {
                        DestroyIcon(
                            handles[0]);
                    }
                }
            }

            // Shortcuts and unusual executables may not expose a 256px group.
            using Drawing.Icon? fallback =
                Drawing.Icon.ExtractAssociatedIcon(
                    appPath);

            if (fallback is null)
                return null;

            using Drawing.Bitmap fallbackBitmap =
                fallback.ToBitmap();

            fallbackBitmap.Save(
                cachePath,
                DrawingImaging.ImageFormat.Png);

            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    private void PixelMenuChooseApp_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not
                System.Windows.Controls.Button button ||
            button.Tag is not int slot)
        {
            return;
        }

        var dialog =
            new Microsoft.Win32.OpenFileDialog
            {
                Title =
                    L(
                        $"Choose app for slot {slot + 1}",
                        $"Chọn app cho ô {slot + 1}"),
                Filter =
                    "Applications|*.exe;*.lnk|Executable|*.exe|Shortcut|*.lnk",
                CheckFileExists = true,
                Multiselect = false
            };

        if (dialog.ShowDialog() != true)
            return;

        string appPath =
            dialog.FileName;

        ActionScriptDefinition? action =
            _actionScripts.FirstOrDefault(
                script =>
                    script.Steps.Count == 1 &&
                    script.Steps[0].Type.Equals(
                        "Run",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        script.Steps[0].Value,
                        appPath,
                        StringComparison.OrdinalIgnoreCase));

        if (action is null)
        {
            int actionId =
                ActionScriptStore
                    .NextAvailableActionId(
                        _actionScripts);

            if (actionId == 0)
            {
                PixelMenuStatusText.Text =
                    L(
                        "All 32 Lumi Action slots are already used.",
                        "Đã dùng hết 32 Lumi Action.");
                return;
            }

            action =
                new ActionScriptDefinition
                {
                    ActionId = actionId,
                    Name =
                        IO.Path.GetFileNameWithoutExtension(
                            appPath),
                    Steps =
                    [
                        new ActionScriptStep
                        {
                            Type = "Run",
                            Value = appPath
                        }
                    ]
                };

            _actionScripts.Add(action);
            ActionScriptStore.Save(
                _actionScripts);
        }

        PixelProMainMenuSlot target =
            PixelMenuProfile
                .Slots[
                    Math.Clamp(
                        slot,
                        0,
                        PixelProMainMenuStore.SlotCount - 1)];

        target.ActionId =
            action.ActionId;

        target.AppPath =
            appPath;

        string? extractedIcon =
            ExtractPixelMenuAppIcon(
                appPath);

        target.IconPath =
            extractedIcon;

        target.AutoIcon =
            !string.IsNullOrWhiteSpace(
                extractedIcon);

        PixelProMainMenuStore.Save(
            _pixelMainMenu);

        SetPixelHomePreviewMode(true);
        RefreshActionScriptsUi(action.Id);
        RefreshPixelMainMenuUi();

        PixelMenuStatusText.Text =
            string.IsNullOrWhiteSpace(extractedIcon)
                ? L(
                    $"Assigned {action.Name}. No EXE icon was available, so the action name will be shown.",
                    $"Đã gán {action.Name}. Không lấy được icon EXE nên màn hình sẽ hiện tên action.")
                : L(
                    $"Assigned {action.Name} with its app icon.",
                    $"Đã gán {action.Name} kèm icon của app.");
    }

    private void PixelMenuChooseIcon_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not
                System.Windows.Controls.Button button ||
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

            PixelProMainMenuSlot target =
                PixelMenuProfile
                    .Slots[
                        Math.Clamp(
                            slot,
                            0,
                            PixelProMainMenuStore.SlotCount - 1)];

            target.IconPath =
                dialog.FileName;

            target.AutoIcon =
                false;

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
        if (sender is not
                System.Windows.Controls.Button button ||
            button.Tag is not int slot)
        {
            return;
        }

        PixelProMainMenuSlot target =
            PixelMenuProfile
                .Slots[
                    Math.Clamp(
                        slot,
                        0,
                        PixelProMainMenuStore.SlotCount - 1)];

        target.IconPath =
            null;
        target.AutoIcon =
            false;

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

        PixelMenuSaveButton.IsEnabled =
            false;

        PixelMenuUploadProgress.Value =
            0;

        int profileIndex =
            PixelMenuProfileIndex;

        PixelProMainMenuProfile profile =
            PixelMenuProfile;

        try
        {
            PixelMenuStatusText.Text =
                L(
                    $"Preparing Main Menu for Keymap Profile {profileIndex + 1:00}…",
                    $"Đang xử lý Main Menu cho Keymap Profile {profileIndex + 1:00}…");

            const int TotalOperations =
                11;

            int completed =
                0;

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
                    profile.BackgroundPath) &&
                IO.File.Exists(
                    profile.BackgroundPath))
            {
                byte[] background =
                    await Task.Run(
                        () =>
                            PixelProMainMenuMediaService
                                .CreateBackgroundJpeg(
                                    profile.BackgroundPath,
                                    profile.BlurPercent,
                                    profile.OpacityPercent,
                                    profile.ScaleMode));

                if (!await pixel
                        .UploadMainMenuBackgroundAsync(
                            profileIndex,
                            background))
                {
                    throw new InvalidOperationException(
                        "PIXEL PRO rejected the profile background.");
                }
            }
            else if (!await pixel
                         .ClearMainMenuBackgroundAsync(
                             profileIndex))
            {
                throw new InvalidOperationException(
                    "PIXEL PRO could not clear the profile background.");
            }

            ReportOperation();

            string[] labels =
                profile.Slots
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

            if (!await pixel
                    .SetMainMenuProfileAsync(
                        profileIndex,
                        profile.Slots
                            .Select(x => x.ActionId)
                            .ToArray(),
                        labels))
            {
                throw new InvalidOperationException(
                    $"PIXEL PRO rejected Main Menu Profile {profileIndex + 1:00}.");
            }

            ReportOperation();

            var iconCache =
                new Dictionary<string, byte[]>(
                    StringComparer.OrdinalIgnoreCase);

            for (int slot = 0;
                 slot < PixelProMainMenuStore.SlotCount;
                 slot++)
            {
                string? iconPath =
                    profile.Slots[slot].IconPath;

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
                                        .CreateIconAsset(
                                            iconPath));

                        iconCache[iconPath] =
                            iconBytes;
                    }

                    if (!await pixel
                            .UploadMainMenuIconAsync(
                                profileIndex,
                                slot,
                                iconBytes))
                    {
                        throw new InvalidOperationException(
                            $"PIXEL PRO rejected icon Profile {profileIndex + 1:00} / Slot {slot + 1}.");
                    }
                }
                else if (!await pixel
                             .ClearMainMenuIconAsync(
                                 profileIndex,
                                 slot))
                {
                    throw new InvalidOperationException(
                        $"PIXEL PRO could not clear icon Profile {profileIndex + 1:00} / Slot {slot + 1}.");
                }

                ReportOperation();
            }

            pixel.SetProfileLayer(
                profileIndex,
                _pixelSelectedLayer);

            if (!await pixel.ShowMainMenuAsync())
            {
                throw new InvalidOperationException(
                    "PIXEL PRO did not show the main menu.");
            }

            ReportOperation();

            PixelProMainMenuStore.Save(
                _pixelMainMenu);

            PixelMenuUploadProgress.Value =
                100;

            PixelMenuStatusText.Text =
                L(
                    $"Main Menu saved for Keymap Profile {profileIndex + 1:00}.",
                    $"Đã lưu Main Menu cho Keymap Profile {profileIndex + 1:00}.");

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

        pixel.SetProfileLayer(
            PixelMenuProfileIndex,
            _pixelSelectedLayer);

        bool ok =
            await pixel.ShowMainMenuAsync();

        PixelMenuStatusText.Text =
            ok
                ? L(
                    $"Showing Main Menu for Keymap Profile {PixelMenuProfileIndex + 1:00}.",
                    $"Đang hiển thị Main Menu của Keymap Profile {PixelMenuProfileIndex + 1:00}.")
                : L(
                    "PIXEL PRO did not confirm the main menu.",
                    "PIXEL PRO chưa xác nhận Main Menu.");
    }
}
