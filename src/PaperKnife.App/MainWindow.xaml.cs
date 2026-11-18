using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using PaperKnife.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace PaperKnife.App;

/// <summary>
/// The editor window. Engine calls run on background threads; while a command runs, every
/// control that could start another engine call is disabled, and the UI thread only reads
/// <see cref="_state"/>, a copy of the session's state taken when the last command finished.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly double[] ZoomLevels = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0];
    private const long MaxDisplayPixels = 40_000_000;

    private DocumentSession? _session;
    private SessionState _state;
    private PdfTextRun? _selected;
    private IReadOnlyList<string> _pageBlockers = [];
    private IReadOnlyList<PdfTextRun> _runs = [];
    private int _pageIndex;
    private int _zoomStep = 2;
    private int _renderedWidth;
    private int _renderedHeight;
    private int _renderRequest;
    private double _rasterScale = 1.0;
    private bool _busy;
    private bool _suppressPageSelection;
    private bool _suppressRunSelection;
    private bool _closeConfirmed;
    private bool _signatureWarningAccepted;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PaperKnife.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        AppWindow.Closing += AppWindow_Closing;
        UpdateChrome();
    }

    public async void OpenOnLaunch(string path)
    {
        try
        {
            await OpenAsync(path);
        }
        catch (Exception ex)
        {
            ShowUnexpectedError(ex);
        }
    }

    public void ShowUnexpectedError(Exception exception) =>
        ShowMessage(InfoBarSeverity.Error, "Something went wrong", $"{exception.Message} Your document wasn't changed by the failed action.");

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.XamlRoot.Changed += async (_, _) =>
        {
            if (_session is not null && Math.Abs(Root.XamlRoot.RasterizationScale - _rasterScale) > 0.01)
            {
                await RenderPageAsync();
            }
        };
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        if (result is not null)
        {
            await OpenAsync(result.Path);
        }
    }

    private async Task OpenAsync(string path)
    {
        if (_busy || !await ConfirmDiscardChangesAsync())
        {
            return;
        }

        string? password = null;
        while (true)
        {
            DocumentSession? session = null;
            SetBusy(true, "Opening…");
            try
            {
                session = await Task.Run(() => DocumentSession.Open(path, password));
            }
            catch (PdfException ex) when (ex.ErrorCode == PdfErrorCode.PasswordRequired)
            {
                // Ask for the password below, once the busy state is cleared.
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                ShowMessage(InfoBarSeverity.Error, "Couldn't open the file", ex is OutOfMemoryException ? "The file is too large to open." : ex.Message);
                return;
            }
            finally
            {
                SetBusy(false);
            }

            if (session is not null)
            {
                await ShowDocumentAsync(session);
                return;
            }

            password = await AskPasswordAsync(Path.GetFileName(path), retry: password is not null);
            if (password is null)
            {
                return;
            }
        }
    }

    private async Task ShowDocumentAsync(DocumentSession session)
    {
        _session?.Dispose();
        _session = session;
        _pageIndex = 0;
        _signatureWarningAccepted = false;
        RefreshState();
        Select(null);
        RebuildPageList();
        await RenderPageAsync();
        var signed = _state.SignatureCount > 0;
        ShowMessage(
            signed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            Path.GetFileName(session.FilePath) ?? "Document",
            signed ? "This PDF is digitally signed. Any edit will invalidate its signature." : "Click any text on the page to edit it.");
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task<bool> SaveAsAsync()
    {
        var session = _session;
        if (session is null || _busy)
        {
            return false;
        }

        var suggestedName = session.FilePath is { } current
            ? $"{Path.GetFileNameWithoutExtension(current)} (edited).pdf"
            : "document.pdf";
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return false;
        }

        SetBusy(true, "Saving…");
        try
        {
            await Task.Run(() => session.SaveAs(result.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(InfoBarSeverity.Error, "Couldn't save", ex.Message);
            return false;
        }
        finally
        {
            RefreshState();
            SetBusy(false);
        }

        ShowMessage(InfoBarSeverity.Success, "Saved", result.Path);
        return true;
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ChangeHistoryAsync(undo: true);

    private async void Redo_Click(object sender, RoutedEventArgs e) => await ChangeHistoryAsync(undo: false);

    private async Task ChangeHistoryAsync(bool undo)
    {
        var session = _session;
        if (session is null || _busy || !(undo ? _state.CanUndo : _state.CanRedo))
        {
            return;
        }

        var selectedObject = _selected?.ObjectIndex;
        SetBusy(true, undo ? "Undoing…" : "Redoing…");
        try
        {
            await Task.Run(() => undo ? session.Undo() : session.Redo());
        }
        finally
        {
            RefreshState();
            SetBusy(false);
        }

        _pageIndex = Math.Min(_pageIndex, _state.PageCount - 1);
        RebuildPageList();
        var page = _pageIndex;
        var reselected = selectedObject is { } index ? await Task.Run(() => session.GetTextRun(page, index)) : null;
        Select(reselected);
        Banner.IsOpen = false;
        await RenderPageAsync();
    }

    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        var page = _pageIndex;
        await RunPageCommandAsync(s => s.RotatePage(page, 1), "Rotating…");
    }

    private async void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        var page = _pageIndex;
        if (_session is null || _busy
            || await ShowDialogAsync($"Delete page {page + 1}?", "You can undo this until you close the document.", "Delete") != ContentDialogResult.Primary)
        {
            return;
        }

        await RunPageCommandAsync(s => s.DeletePage(page), "Deleting page…");
    }

    private async void Append_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _busy)
        {
            return;
        }

        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        if (result is not null)
        {
            var path = result.Path;
            await RunPageCommandAsync(s => s.AppendDocument(PdfSnapshot.FromFile(path)), "Adding pages…");
        }
    }

    private async Task RunPageCommandAsync(Func<DocumentSession, EditResult> command, string status)
    {
        var session = _session;
        if (session is null || _busy)
        {
            return;
        }

        Select(null);
        SetBusy(true, status);
        EditResult result;
        try
        {
            result = await Task.Run(() => command(session));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowMessage(InfoBarSeverity.Error, "Nothing was changed", ex.Message);
            return;
        }
        finally
        {
            RefreshState();
            SetBusy(false);
        }

        ShowResult(result);
        _pageIndex = Math.Min(_pageIndex, _state.PageCount - 1);
        RebuildPageList();
        await RenderPageAsync();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyEditAsync();

    private async Task ApplyEditAsync()
    {
        var session = _session;
        var run = _selected;
        if (session is null || run is null || _busy || !ApplyButton.IsEnabled)
        {
            return;
        }

        if (_state.SignatureCount > 0 && !_signatureWarningAccepted)
        {
            if (await ShowDialogAsync("Edit a signed PDF?", "This PDF is digitally signed. Editing it will invalidate the signature.", "Edit anyway") != ContentDialogResult.Primary)
            {
                return;
            }

            _signatureWarningAccepted = true;
        }

        var newText = EditBox.Text;
        SetBusy(true, "Applying and verifying…");
        EditResult result;
        try
        {
            result = await Task.Run(() => session.ReplaceText(run.PageIndex, run.ObjectIndex, newText));
        }
        catch (PdfException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Nothing was changed", ex.Message);
            return;
        }
        finally
        {
            RefreshState();
            SetBusy(false);
        }

        ShowResult(result);
        if (result.Status == EditStatus.Applied)
        {
            Select(result.TextRun);
            await RenderPageAsync();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        EditBox.Text = _selected?.Text ?? string.Empty;
    }

    private async void EditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ApplyEditAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EditBox.Text = _selected?.Text ?? string.Empty;
        }
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChrome();

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await ZoomAsync(+1);

    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await ZoomAsync(-1);

    private async Task ZoomAsync(int delta)
    {
        var step = Math.Clamp(_zoomStep + delta, 0, ZoomLevels.Length - 1);
        if (!_busy && step != _zoomStep)
        {
            _zoomStep = step;
            await RenderPageAsync();
        }
    }

    private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPageSelection || _busy || _session is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex == _pageIndex)
        {
            return;
        }

        _pageIndex = PageList.SelectedIndex;
        Select(null);
        await RenderPageAsync();
    }

    private async void PageImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var session = _session;
        if (session is null || _busy || _renderedWidth == 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(PageImage).Position;
        var (page, width, height) = (_pageIndex, _renderedWidth, _renderedHeight);
        var x = (int)(position.X * _rasterScale);
        var y = (int)(position.Y * _rasterScale);
        var run = await Task.Run(() => session.HitTestText(page, width, height, x, y));
        if (session == _session && page == _pageIndex && !_busy)
        {
            Select(run, focusEditor: true);
        }
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressRunSelection && !_busy && RunList.SelectedIndex >= 0 && RunList.SelectedIndex < _runs.Count)
        {
            Select(_runs[RunList.SelectedIndex]);
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in PaperKnife";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        string? path;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            path = items.OfType<StorageFile>()
                .FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))?.Path;
        }
        finally
        {
            deferral.Complete();
        }

        if (path is not null)
        {
            await OpenAsync(path);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (_busy)
        {
            // Never trap the user: an engine call could take very long on a malformed file.
            args.Cancel = true;
            var quit = await ShowDialogAsync(
                "PaperKnife is still working",
                "Wait for the current action to finish, or quit now. Unsaved changes will be lost if you quit.",
                "Quit anyway");
            if (quit == ContentDialogResult.Primary)
            {
                _closeConfirmed = true;
                Close();
            }

            return;
        }

        if (!_state.IsModified)
        {
            return;
        }

        args.Cancel = true;
        if (await ConfirmDiscardChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Returns true when it's fine to drop the current document (saved, discarded or unmodified).</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_session is not { } session || !_state.IsModified)
        {
            return true;
        }

        var name = Path.GetFileName(session.FilePath) ?? "this document";
        var choice = await ShowDialogAsync($"Save changes to {name}?", "Your changes will be lost if you don't save them.", "Save as…", "Don't save");
        return choice switch
        {
            ContentDialogResult.Primary => await SaveAsAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async Task<string?> AskPasswordAsync(string fileName, bool retry)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = retry ? "That password didn't work. Try again." : $"“{fileName}” is protected with a password.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        return await ShowDialogAsync("Password required", panel, "Open") == ContentDialogResult.Primary ? box.Password : null;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(string title, object content, string primary, string? secondary = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (secondary is not null)
        {
            dialog.SecondaryButtonText = secondary;
        }

        return await dialog.ShowAsync();
    }

    private async Task RenderPageAsync()
    {
        var session = _session;
        if (session is null)
        {
            PageImage.Source = null;
            UpdateChrome();
            return;
        }

        var page = _pageIndex;
        var selected = _selected;
        var raster = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var pixelsPerPoint = ZoomLevels[_zoomStep] * 96.0 / 72.0 * raster;
        var request = ++_renderRequest;
        PageRendering rendering;
        try
        {
            rendering = await Task.Run(() =>
            {
                var size = session.GetPageSize(page);
                var scale = Math.Min(pixelsPerPoint, Math.Sqrt(MaxDisplayPixels / (size.Width * (double)size.Height)));
                var width = Math.Max(1, (int)Math.Round(size.Width * scale));
                var height = Math.Max(1, (int)Math.Round(size.Height * scale));
                var runs = session.GetTextRuns(page).Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
                var selection = selected?.PageIndex == page
                    ? session.MapToPixels(page, selected.Bounds.Inflate(1f), width, height)
                    : (PixelRect?)null;
                return new PageRendering(session.RenderPage(page, width, height), session.GetTextEditBlockers(page), runs, selection);
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            // The document or page went away while rendering; a newer render follows.
            return;
        }
        catch (PdfException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Couldn't draw this page", ex.Message);
            return;
        }

        if (request != _renderRequest || session != _session || page != _pageIndex)
        {
            return;
        }

        var bitmap = rendering.Bitmap;
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var stream = image.PixelBuffer.AsStream())
        {
            stream.Write(bitmap.Pixels);
        }

        image.Invalidate();
        PageImage.Source = image;
        PageImage.Width = bitmap.Width / raster;
        PageImage.Height = bitmap.Height / raster;
        Overlay.Width = PageImage.Width;
        Overlay.Height = PageImage.Height;
        _renderedWidth = bitmap.Width;
        _renderedHeight = bitmap.Height;
        _rasterScale = raster;
        _pageBlockers = rendering.Blockers;
        _runs = rendering.Runs;
        RebuildRunList();
        if (ReferenceEquals(selected, _selected))
        {
            ShowSelectionBox(rendering.Selection);
        }
        else
        {
            UpdateSelectionBox();
        }

        UpdateChrome();
    }

    private void RebuildPageList()
    {
        _suppressPageSelection = true;
        PageList.Items.Clear();
        var count = _session is null ? 0 : _state.PageCount;
        for (var i = 0; i < count; i++)
        {
            PageList.Items.Add($"Page {i + 1}");
        }

        PageList.SelectedIndex = count == 0 ? -1 : _pageIndex;
        _suppressPageSelection = false;
    }

    private void RebuildRunList()
    {
        _suppressRunSelection = true;
        RunList.Items.Clear();
        foreach (var run in _runs)
        {
            var text = run.Text.Length > 80 ? string.Concat(run.Text.AsSpan(0, 79), "…") : run.Text;
            RunList.Items.Add(run.IsInvisible ? $"{text} (invisible)" : text);
        }

        SyncRunListSelection();
        _suppressRunSelection = false;
    }

    private void SyncRunListSelection()
    {
        var wasSuppressed = _suppressRunSelection;
        _suppressRunSelection = true;
        RunList.SelectedIndex = _selected is null
            ? -1
            : _runs.ToList().FindIndex(r => r.ObjectIndex == _selected.ObjectIndex && r.PageIndex == _selected.PageIndex);
        _suppressRunSelection = wasSuppressed;
    }

    private void Select(PdfTextRun? run, bool focusEditor = false)
    {
        _selected = run;
        EditBox.Text = run?.Text ?? string.Empty;
        FontInfo.Text = run is null ? string.Empty : Describe(run);
        SyncRunListSelection();
        UpdateSelectionBox();
        UpdateChrome();
        if (focusEditor && run is not null && EditBox.IsEnabled)
        {
            EditBox.Focus(FocusState.Programmatic);
            EditBox.SelectAll();
        }
    }

    private static string Describe(PdfTextRun run)
    {
        var embedding = run.IsFontSubset ? "embedded subset" : run.IsFontEmbedded ? "embedded" : "not embedded";
        var details = $"Font: {run.FontName} · {run.FontSize:0.#} pt · {embedding}";
        return run.IsInvisible ? $"{details} · invisible text layer (for example from OCR)" : details;
    }

    /// <summary>Maps the selection to pixels on a background thread, then draws the box.</summary>
    private async void UpdateSelectionBox()
    {
        var session = _session;
        var run = _selected;
        var (page, width, height) = (_pageIndex, _renderedWidth, _renderedHeight);
        if (session is null || run is null || width == 0 || run.PageIndex != page)
        {
            ShowSelectionBox(null);
            return;
        }

        PixelRect rect;
        try
        {
            rect = await Task.Run(() => session.MapToPixels(page, run.Bounds.Inflate(1f), width, height));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException or PdfException)
        {
            return;
        }

        if (ReferenceEquals(run, _selected) && session == _session && width == _renderedWidth && height == _renderedHeight)
        {
            ShowSelectionBox(rect);
        }
    }

    private void ShowSelectionBox(PixelRect? rect)
    {
        if (rect is not { } r)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(SelectionBox, r.X / _rasterScale);
        Canvas.SetTop(SelectionBox, r.Y / _rasterScale);
        SelectionBox.Width = Math.Max(2, r.Width / _rasterScale);
        SelectionBox.Height = Math.Max(2, r.Height / _rasterScale);
        SelectionBox.Visibility = Visibility.Visible;
    }

    private void ShowResult(EditResult result)
    {
        switch (result.Status)
        {
            case EditStatus.Applied:
                var warned = result.Warnings.Count > 0;
                var body = string.Join(" ", result.Warnings.Concat(result.Details));
                ShowMessage(warned ? InfoBarSeverity.Warning : InfoBarSeverity.Success, result.Message, body.Length > 0 ? body : null);
                break;
            case EditStatus.NoChange:
                ShowMessage(InfoBarSeverity.Informational, result.Message, null);
                break;
            default:
                ShowMessage(InfoBarSeverity.Error, "Nothing was changed", result.Message);
                break;
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string? message)
    {
        Banner.Severity = severity;
        Banner.Title = title;
        Banner.Message = message ?? string.Empty;
        Banner.IsOpen = true;
    }

    /// <summary>Copies the session's state. Call only when no command is running.</summary>
    private void RefreshState()
    {
        try
        {
            _state = _session is { } session
                ? new SessionState(session.PageCount, session.CanUndo, session.CanRedo, session.IsModified, session.SignatureCount)
                : default;
        }
        catch (ObjectDisposedException)
        {
            // Never let a state refresh keep the window in its busy state.
            _state = default;
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateChrome();
        if (busy && status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void UpdateChrome()
    {
        var session = _session;
        var open = session is not null;
        var idle = !_busy;

        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Workspace.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = idle;
        SaveButton.IsEnabled = open && idle;
        UndoButton.IsEnabled = open && idle && _state.CanUndo;
        RedoButton.IsEnabled = open && idle && _state.CanRedo;
        RotateButton.IsEnabled = open && idle;
        DeletePageButton.IsEnabled = open && idle;
        AppendButton.IsEnabled = open && idle;
        ZoomInButton.IsEnabled = open && idle && _zoomStep < ZoomLevels.Length - 1;
        ZoomOutButton.IsEnabled = open && idle && _zoomStep > 0;
        PageList.IsEnabled = open && idle;
        RunList.IsEnabled = open && idle;

        var editable = open && _selected is not null && _pageBlockers.Count == 0;
        EditBox.IsEnabled = editable && idle;
        var changed = editable && !string.Equals(EditBox.Text, _selected!.Text, StringComparison.Ordinal);
        ApplyButton.IsEnabled = changed && idle;
        ResetButton.IsEnabled = changed && idle;
        PageBlockedBar.IsOpen = open && _pageBlockers.Count > 0;
        PageBlockedBar.Message = string.Join(" ", _pageBlockers);
        EditHint.Text = _selected is null
            ? "Click any text on the page to select it."
            : "Change the text, then press Enter or select Apply.";

        var name = session is null ? null : Path.GetFileName(session.FilePath) ?? "Untitled";
        Title = name is null ? "PaperKnife" : $"{name}{(_state.IsModified ? " •" : string.Empty)} – PaperKnife";
        if (idle)
        {
            StatusText.Text = session is null
                ? "Your files stay on this device."
                : $"Page {_pageIndex + 1} of {_state.PageCount} · {ZoomLevels[_zoomStep]:P0}{(_state.IsModified ? " · Unsaved changes" : string.Empty)}";
        }
    }

    private readonly record struct SessionState(int PageCount, bool CanUndo, bool CanRedo, bool IsModified, int SignatureCount);

    private sealed record PageRendering(RenderedBitmap Bitmap, IReadOnlyList<string> Blockers, IReadOnlyList<PdfTextRun> Runs, PixelRect? Selection);
}
