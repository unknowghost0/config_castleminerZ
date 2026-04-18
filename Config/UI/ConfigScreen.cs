using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework;
using DNA.Net.GamerServices;
using DNA.CastleMinerZ;
using DNA.Drawing.UI;
using DNA.Drawing;
using DNA.Input;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System;

namespace Config
{
    internal sealed class ConfigScreen : Screen
    {
        internal static bool IsOpen { get; private set; }

        private sealed class ConfigFileItem
        {
            public string FullPath;
            public string DisplayPath;
        }

        private readonly CastleMinerZGame _game;

        private Texture2D _white;
        private SpriteFont _titleFont;
        private SpriteFont _bodyFont;
        private SpriteFont _smallFont;
        private bool _initialized;
        private bool _focusEditor = true;

        private Rectangle _panelRect;
        private Rectangle _filesRect;
        private Rectangle _editorRect;
        private Rectangle _statusRect;
        private Rectangle _saveRect;
        private Rectangle _reloadRect;
        private Rectangle _refreshRect;
        private Rectangle _closeRect;
        private Rectangle _fileScrollbarTrackRect;
        private Rectangle _fileScrollbarThumbRect;
        private Rectangle _fileFilterRect;

        private readonly List<ConfigFileItem> _files = new List<ConfigFileItem>();
        private readonly List<int> _visibleFileIndices = new List<int>();
        private int _selectedFileIndex = -1;
        private int _fileScroll;
        private int _editorTopLine;
        private int _caretIndex;
        private int _preferredColumn = -1;

        private bool _dirty;
        private string _editorText = "";
        private string _lastLoadedText = "";
        private string _statusMessage = "Browse files on the left, edit text on the right, then press Ctrl+S to save.";
        private Color _statusColor = Color.LightGray;
        private double _blinkTime;
        private bool _mouseLeftWasDown;
        private bool _fileScrollbarVisible;
        private bool _draggingFileScrollbar;
        private int _fileScrollbarDragStartMouseY;
        private int _fileScrollbarDragStartScroll;
        private readonly List<string> _cachedLines = new List<string>();
        private readonly List<int> _lineStarts = new List<int>();
        private int _lastHotReloadPollTick;
        private ulong _lastFileListFingerprint;
        private DateTime _selectedFileLastWriteUtc = DateTime.MinValue;
        private string _fileFilterText = "";
        private bool _focusFileFilter;

        private const int ButtonHeight = 36;
        private const int ButtonWidth = 140;
        private const int ButtonGap = 12;
        private const int PanePadding = 20;
        private const int FileRowPadding = 6;
        private const int MaxEditableBytes = 524288;
        private const int FileRowGap = 4;
        private const int FileHeaderExtraHeight = 4;
        private const int FileContentTopGap = 4;
        private const int FileScrollbarWidth = 12;
        private const int FileScrollbarMargin = 6;
        private const int FileScrollbarMinThumbHeight = 28;
        private const int HotReloadPollMs = 1000;
        private const int InputBoxHeight = 30;

        public ConfigScreen(CastleMinerZGame game) : base(true, true)
        {
            _game = game;
            ShowMouseCursor = true;
            CaptureMouse = false;
        }

        public override void OnPushed()
        {
            base.OnPushed();
            IsOpen = true;
            RefreshFileList();
        }

        public override void OnPoped()
        {
            base.OnPoped();
            IsOpen = false;
            CaptureMouse = false;
        }

        protected override void OnDraw(GraphicsDevice device, SpriteBatch spriteBatch, GameTime gameTime)
        {
            EnsureInit(device);
            Layout(device);
            PollHotReload();

            _blinkTime = gameTime.TotalGameTime.TotalSeconds;

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);

            spriteBatch.Draw(_white, Screen.Adjuster.ScreenRect, new Color(0, 0, 0, 190));
            DrawPanel(spriteBatch);
            DrawTitle(spriteBatch);
            DrawStatus(spriteBatch);
            DrawButtons(spriteBatch);
            DrawFiles(spriteBatch);
            DrawEditor(spriteBatch);

            spriteBatch.End();
        }

        protected override bool OnChar(GameTime gameTime, char c)
        {
            if (_focusFileFilter)
            {
                if (!char.IsControl(c))
                {
                    _fileFilterText += c;
                    RebuildVisibleFileIndices(true);
                }

                return false;
            }

            if (_selectedFileIndex < 0)
                return base.OnChar(gameTime, c);

            if (c == '\r' || c == '\n')
                return false;

            if (c == '\t')
            {
                InsertText("    ");
                return false;
            }

            if (!char.IsControl(c))
            {
                InsertText(c.ToString());
                return false;
            }

            return base.OnChar(gameTime, c);
        }

        protected override bool OnPlayerInput(InputManager input, GameController controller, KeyboardInput chatpad, GameTime gameTime)
        {
            if (!CastleMinerZGame.Instance.IsActive)
                return false;

            var mousePoint = input.Mouse.Position;
            bool leftDown = input.Mouse.LeftButtonDown;
            bool leftPressed = input.Mouse.LeftButtonPressed || (leftDown && !_mouseLeftWasDown);
            _mouseLeftWasDown = leftDown;

            UpdateFileScrollbarRects();
            HandleFileScrollbar(input.Mouse, mousePoint);

            var ks = Keyboard.GetState();
            bool ctrlDown = ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl);

            if (input.Keyboard.WasKeyPressed(Keys.Escape))
            {
                if (_dirty)
                {
                    SetStatus("Save or reload the current file before closing.", Color.Yellow);
                    return false;
                }

                PopMe();
                return false;
            }

            if (ctrlDown && input.Keyboard.WasKeyPressed(Keys.S))
            {
                SaveCurrentFile();
                return false;
            }

            if (input.Keyboard.WasKeyPressed(Keys.F5))
            {
                RefreshFileList();
                return false;
            }

            if (input.Mouse.DeltaWheel != 0 && _filesRect.Contains(mousePoint))
            {
                _focusEditor = false;
                ScrollFileListBy(-Math.Sign(input.Mouse.DeltaWheel) * 3);
                return false;
            }

            if (_focusFileFilter)
            {
                if (input.Keyboard.WasKeyPressed(Keys.Back) && _fileFilterText.Length > 0)
                {
                    _fileFilterText = _fileFilterText.Substring(0, _fileFilterText.Length - 1);
                    RebuildVisibleFileIndices(true);
                    return false;
                }

                if (input.Keyboard.WasKeyPressed(Keys.Delete) && _fileFilterText.Length > 0)
                {
                    _fileFilterText = "";
                    RebuildVisibleFileIndices(true);
                    return false;
                }
            }

            HandleMouse(mousePoint, leftPressed);
            if (_focusEditor && _selectedFileIndex >= 0)
            {
                HandleEditorKeys(input, mousePoint);
            }
            else if (!_focusFileFilter)
            {
                HandleFileListKeys(input);
            }

            return false;
        }

        private void EnsureInit(GraphicsDevice device)
        {
            if (_initialized)
                return;

            _white = new Texture2D(device, 1, 1);
            _white.SetData(new[] { Color.White });

            _titleFont = _game._largeFont ?? _game._medFont;
            _bodyFont = _game._medFont ?? _game._smallFont;
            _smallFont = _game._smallFont ?? _game._medFont;

            _initialized = true;
        }

        private void Layout(GraphicsDevice device)
        {
            var safe = device.Viewport.Bounds;
            int panelW = (int)(safe.Width * 0.88f);
            int panelH = (int)(safe.Height * 0.84f);
            int panelX = safe.Center.X - panelW / 2;
            int panelY = safe.Center.Y - panelH / 2;

            _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

            int titleHeight = _titleFont.LineSpacing + 18;
            int statusHeight = _smallFont.LineSpacing * 2 + 18;
            int buttonsY = _panelRect.Bottom - PanePadding - ButtonHeight;
            int contentTop = _panelRect.Top + PanePadding + titleHeight;
            int contentBottom = buttonsY - PanePadding;
            int contentHeight = Math.Max(120, contentBottom - contentTop - statusHeight - 8);

            int filesWidth = (int)(_panelRect.Width * 0.30f);
            _filesRect = new Rectangle(_panelRect.Left + PanePadding, contentTop, filesWidth, contentHeight);
            _editorRect = new Rectangle(_filesRect.Right + PanePadding, contentTop, _panelRect.Right - _filesRect.Right - PanePadding * 2, contentHeight);
            _statusRect = new Rectangle(_panelRect.Left + PanePadding, contentTop + contentHeight + 8, _panelRect.Width - PanePadding * 2, statusHeight);
            _fileFilterRect = new Rectangle(_filesRect.Left + 8, _filesRect.Top + GetFileRowHeight() + FileHeaderExtraHeight + 6, _filesRect.Width - 16, InputBoxHeight);
            int closeX = _panelRect.Right - PanePadding - ButtonWidth;
            _closeRect = new Rectangle(closeX, buttonsY, ButtonWidth, ButtonHeight);
            _refreshRect = new Rectangle(_closeRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _reloadRect = new Rectangle(_refreshRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _saveRect = new Rectangle(_reloadRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
        }

        private void DrawPanel(SpriteBatch sb)
        {
            var bg = new Color(18, 21, 27, 235);
            var border = new Color(84, 115, 164, 255);
            var pane = new Color(26, 31, 39, 255);

            sb.Draw(_white, _panelRect, bg);
            DrawBorder(sb, _panelRect, border);

            sb.Draw(_white, _filesRect, pane);
            sb.Draw(_white, _editorRect, pane);
            sb.Draw(_white, _statusRect, new Color(20, 24, 31, 255));
            DrawBorder(sb, _filesRect, new Color(62, 75, 95, 255));
            DrawBorder(sb, _editorRect, new Color(62, 75, 95, 255));
            DrawBorder(sb, _statusRect, new Color(62, 75, 95, 255));
        }

        private void DrawTitle(SpriteBatch sb)
        {
            float sy = Screen.Adjuster.ScaleFactor.Y;
            var titlePos = new Vector2(_panelRect.Left + PanePadding, _panelRect.Top + PanePadding - 2);
            var subPos = new Vector2(_panelRect.Left + PanePadding, titlePos.Y + _titleFont.LineSpacing * sy);

            sb.DrawString(_titleFont, "Config", titlePos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_titleFont, "Config", titlePos, Color.White);

            string runtimeRoot = GetRuntimeModsRoot();
            string subtitle = string.IsNullOrEmpty(runtimeRoot)
                ? "No !Mods folder found yet."
                : "Editing live files from: " + runtimeRoot;

            sb.DrawString(_smallFont, subtitle, subPos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, subtitle, subPos, new Color(200, 210, 225, 255));
        }

        private void DrawStatus(SpriteBatch sb)
        {
            var line1 = _statusMessage ?? "";
            var line2 = _selectedFileIndex >= 0
                ? _files[_selectedFileIndex].DisplayPath + (_dirty ? "  [modified]" : "")
                : "No config file selected.";

            var pos1 = new Vector2(_statusRect.Left + 12, _statusRect.Top + 10);
            var pos2 = new Vector2(_statusRect.Left + 12, pos1.Y + _smallFont.LineSpacing);

            sb.DrawString(_smallFont, line1, pos1 + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, line1, pos1, _statusColor);

            sb.DrawString(_smallFont, line2, pos2 + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, line2, pos2, new Color(180, 190, 205, 255));
        }

        private void DrawButtons(SpriteBatch sb)
        {
            DrawButton(sb, _saveRect, "SAVE", _dirty ? new Color(70, 118, 78, 255) : new Color(48, 58, 52, 255));
            DrawButton(sb, _reloadRect, "RELOAD ALL", new Color(58, 65, 80, 255));
            DrawButton(sb, _refreshRect, "REFRESH LIST", new Color(58, 65, 80, 255));
            DrawButton(sb, _closeRect, "CLOSE", new Color(80, 58, 58, 255));
        }

        private void DrawButton(SpriteBatch sb, Rectangle rect, string text, Color fill)
        {
            sb.Draw(_white, rect, fill);
            DrawBorder(sb, rect, new Color(120, 130, 150, 255));
            DrawCenteredString(sb, _smallFont, text, rect, Color.White);
        }

        private void DrawFiles(SpriteBatch sb)
        {
            int rowHeight = GetFileRowHeight();
            int visibleRows = GetVisibleFileRowCount();
            int maxScroll = Math.Max(0, _visibleFileIndices.Count - visibleRows);
            if (_fileScroll > maxScroll) _fileScroll = maxScroll;

            var titleRect = new Rectangle(_filesRect.Left, _filesRect.Top, _filesRect.Width, rowHeight + FileHeaderExtraHeight);
            sb.Draw(_white, titleRect, new Color(34, 42, 54, 255));
            DrawCenteredString(sb, _smallFont, "Config Files", titleRect, Color.White);
            DrawInputBox(sb, _fileFilterRect, _fileFilterText, "Find file...", _focusFileFilter);
            int y = _fileFilterRect.Bottom + FileContentTopGap;
            int clipBottom = _filesRect.Bottom - 6;
            int textRightPadding = _fileScrollbarVisible ? (FileScrollbarWidth + FileScrollbarMargin * 3) : 16;
            for (int row = 0; row < visibleRows; row++)
            {
                int visibleIndex = _fileScroll + row;
                if (visibleIndex >= _visibleFileIndices.Count)
                    break;
                int index = _visibleFileIndices[visibleIndex];

                var itemRect = new Rectangle(_filesRect.Left + 6, y, _filesRect.Width - 12, rowHeight);
                if (itemRect.Bottom > clipBottom)
                    break;

                bool selected = index == _selectedFileIndex;
                var fill = selected ? new Color(70, 98, 145, 255) : new Color(30, 36, 45, 255);
                sb.Draw(_white, itemRect, fill);

                var pos = new Vector2(itemRect.Left + 8, itemRect.Top + FileRowPadding);
                string text = ClipText(_smallFont, _files[index].DisplayPath, itemRect.Width - textRightPadding);
                sb.DrawString(_smallFont, text, pos + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, text, pos, selected ? Color.White : new Color(210, 214, 220, 255));

                y += GetFileRowStep();
            }

            DrawFileScrollbar(sb);

            if (_visibleFileIndices.Count == 0)
            {
                var msg = _files.Count == 0 ? "No config files were found under !Mods yet." : "No files match the filter.";
                var pos = new Vector2(_filesRect.Left + 12, _fileFilterRect.Bottom + 12);
                sb.DrawString(_smallFont, msg, pos + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, msg, pos, Color.LightGray);
            }
        }

        private void DrawEditor(SpriteBatch sb)
        {
            int headerHeight = _smallFont.LineSpacing + 12;
            var headerRect = new Rectangle(_editorRect.Left, _editorRect.Top, _editorRect.Width, headerHeight);
            sb.Draw(_white, headerRect, new Color(34, 42, 54, 255));

            string title = _selectedFileIndex >= 0 ? _files[_selectedFileIndex].DisplayPath : "Editor";
            DrawCenteredString(sb, _smallFont, title, headerRect, Color.White);

            int lineHeight = _smallFont.LineSpacing + 2;
            int contentTop = headerRect.Bottom + 8;
            int visibleLines = Math.Max(1, (_editorRect.Bottom - contentTop - 8) / lineHeight);

            if (_editorTopLine > Math.Max(0, _cachedLines.Count - visibleLines))
                _editorTopLine = Math.Max(0, _cachedLines.Count - visibleLines);

            int lineNumberWidth = 56;
            var lineRect = new Rectangle(_editorRect.Left + 8, contentTop, _editorRect.Width - 16, _editorRect.Height - headerHeight - 16);
            sb.Draw(_white, new Rectangle(lineRect.Left, lineRect.Top, lineNumberWidth, lineRect.Height), new Color(21, 25, 33, 255));

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = _editorTopLine + i;
                if (lineIndex >= _cachedLines.Count)
                    break;

                int y = contentTop + i * lineHeight;
                var numberPos = new Vector2(lineRect.Left + 8, y);
                string number = (lineIndex + 1).ToString(CultureInfo.InvariantCulture);
                sb.DrawString(_smallFont, number, numberPos + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, number, numberPos, new Color(145, 155, 170, 255));

                var textPos = new Vector2(lineRect.Left + lineNumberWidth + 10, y);
                string clipped = ClipText(_smallFont, _cachedLines[lineIndex], lineRect.Width - lineNumberWidth - 20);
                sb.DrawString(_smallFont, clipped, textPos + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, clipped, textPos, new Color(230, 230, 235, 255));
            }

            if (_selectedFileIndex >= 0 && ((int)(_blinkTime * 2) % 2 == 0))
            {
                int caretLine;
                int caretColumn;
                GetCaretLocation(out caretLine, out caretColumn);

                if (caretLine >= _editorTopLine && caretLine < _editorTopLine + visibleLines)
                {
                    string currentLine = caretLine < _cachedLines.Count ? _cachedLines[caretLine] : "";
                    int safeColumn = Math.Min(caretColumn, currentLine.Length);
                    string before = currentLine.Substring(0, safeColumn);
                    float x = lineRect.Left + lineNumberWidth + 10 + _smallFont.MeasureString(before).X;
                    float y = contentTop + (caretLine - _editorTopLine) * lineHeight;
                    sb.Draw(_white, new Rectangle((int)x, (int)y, 2, _smallFont.LineSpacing + 1), Color.White);
                }
            }
        }

        private void HandleMouse(Point mousePoint, bool leftPressed)
        {
            if (!leftPressed)
                return;

            if (HandleFileScrollbarClick(mousePoint))
                return;

            if (_saveRect.Contains(mousePoint))
            {
                SaveCurrentFile();
                return;
            }

            if (_reloadRect.Contains(mousePoint))
            {
                ReloadAllFiles();
                return;
            }

            if (_refreshRect.Contains(mousePoint))
            {
                RefreshFileList();
                return;
            }

            if (_closeRect.Contains(mousePoint))
            {
                if (_dirty)
                {
                    SetStatus("Save or reload the current file before closing.", Color.Yellow);
                    return;
                }

                PopMe();
                return;
            }

            if (_fileFilterRect.Contains(mousePoint))
            {
                _focusEditor = false;
                _focusFileFilter = true;
                return;
            }

            if (_filesRect.Contains(mousePoint))
            {
                _focusEditor = false;
                _focusFileFilter = false;
                ClickFileList(mousePoint);
                return;
            }

            if (_editorRect.Contains(mousePoint) && _selectedFileIndex >= 0)
            {
                _focusEditor = true;
                _focusFileFilter = false;
                PlaceCaretFromMouse(mousePoint);
            }
        }

        private void HandleFileListKeys(InputManager input)
        {
            if (_visibleFileIndices.Count == 0)
                return;

            if (input.Keyboard.WasKeyPressed(Keys.PageUp))
            {
                int selectedVisible = GetSelectedVisibleIndex();
                if (selectedVisible < 0) selectedVisible = 0;
                TrySelectVisibleFile(Math.Max(0, selectedVisible - 10));
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.PageDown))
            {
                int selectedVisible = GetSelectedVisibleIndex();
                if (selectedVisible < 0) selectedVisible = 0;
                TrySelectVisibleFile(Math.Min(_visibleFileIndices.Count - 1, selectedVisible + 10));
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Up))
            {
                int selectedVisible = GetSelectedVisibleIndex();
                int next = selectedVisible < 0 ? 0 : Math.Max(0, selectedVisible - 1);
                TrySelectVisibleFile(next);
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Down))
            {
                int selectedVisible = GetSelectedVisibleIndex();
                int next = selectedVisible < 0 ? 0 : Math.Min(_visibleFileIndices.Count - 1, selectedVisible + 1);
                TrySelectVisibleFile(next);
                return;
            }
        }

        private void HandleEditorKeys(InputManager input, Point mousePoint)
        {
            if (input.Keyboard.WasKeyPressed(Keys.Back))
            {
                if (_caretIndex > 0)
                {
                    _editorText = _editorText.Remove(_caretIndex - 1, 1);
                    _caretIndex--;
                    RebuildTextCache();
                    MarkDirty();
                    EnsureCaretVisible();
                }

                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Delete))
            {
                if (_caretIndex < _editorText.Length)
                {
                    _editorText = _editorText.Remove(_caretIndex, 1);
                    RebuildTextCache();
                    MarkDirty();
                    EnsureCaretVisible();
                }

                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Enter))
            {
                InsertText(Environment.NewLine);
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Left))
            {
                if (_caretIndex > 0)
                    _caretIndex--;
                _preferredColumn = -1;
                EnsureCaretVisible();
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Right))
            {
                if (_caretIndex < _editorText.Length)
                    _caretIndex++;
                _preferredColumn = -1;
                EnsureCaretVisible();
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Home))
            {
                MoveCaretToLineBoundary(true);
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.End))
            {
                MoveCaretToLineBoundary(false);
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Up))
            {
                MoveCaretVertical(-1);
                return;
            }

            if (input.Keyboard.WasKeyPressed(Keys.Down))
            {
                MoveCaretVertical(1);
                return;
            }

            if (input.Mouse.DeltaWheel != 0 && _editorRect.Contains(mousePoint))
            {
                _editorTopLine -= Math.Sign(input.Mouse.DeltaWheel) * 3;
                if (_editorTopLine < 0) _editorTopLine = 0;
                int maxTop = Math.Max(0, _cachedLines.Count - GetVisibleEditorLineCount());
                if (_editorTopLine > maxTop) _editorTopLine = maxTop;
            }
        }

        private void ClickFileList(Point mousePoint)
        {
            int rowHeight = GetFileRowHeight();
            int headerBottom = _fileFilterRect.Bottom + FileContentTopGap;
            if (mousePoint.Y < headerBottom)
                return;

            int row = (mousePoint.Y - headerBottom) / GetFileRowStep();
            TrySelectVisibleFile(_fileScroll + row);
        }

        private void TrySelectVisibleFile(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= _visibleFileIndices.Count)
                return;

            TrySelectFile(_visibleFileIndices[visibleIndex]);
        }

        private int GetSelectedVisibleIndex()
        {
            if (_selectedFileIndex < 0)
                return -1;

            return _visibleFileIndices.IndexOf(_selectedFileIndex);
        }

        private void TrySelectFile(int index)
        {
            if (index < 0 || index >= _files.Count)
                return;

            if (_dirty && index != _selectedFileIndex)
            {
                SetStatus("Save or reload the current file before switching files.", Color.Yellow);
                return;
            }

            _selectedFileIndex = index;
            LoadSelectedFile();
            _focusEditor = true;
            EnsureSelectedFileVisible();
        }

        private void LoadSelectedFile()
        {
            if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
            {
                _editorText = "";
                _lastLoadedText = "";
                _caretIndex = 0;
                _dirty = false;
                RebuildTextCache();
                return;
            }

            var path = _files[_selectedFileIndex].FullPath;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxEditableBytes)
                {
                    _editorText = "";
                    _lastLoadedText = "";
                    _caretIndex = 0;
                    _editorTopLine = 0;
                    _dirty = false;
                    _preferredColumn = -1;
                    RebuildTextCache();
                    SetStatus("Skipped large file (" + info.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes). Config is only for config-sized files.", Color.Yellow);
                    return;
                }

                _editorText = File.Exists(path) ? File.ReadAllText(path) : "";
                _lastLoadedText = _editorText;
                _caretIndex = 0;
                _editorTopLine = 0;
                _dirty = false;
                _preferredColumn = -1;
                RebuildTextCache();
                _selectedFileLastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                SetStatus("Loaded " + _files[_selectedFileIndex].DisplayPath, Color.LightGreen);
            }
            catch (Exception ex)
            {
                _editorText = "";
                _lastLoadedText = "";
                _caretIndex = 0;
                _dirty = false;
                RebuildTextCache();
                _selectedFileLastWriteUtc = DateTime.MinValue;
                SetStatus("Failed to load file: " + ex.Message, Color.OrangeRed);
            }
        }

        private void SaveCurrentFile()
        {
            if (_selectedFileIndex < 0)
            {
                SetStatus("Pick a config file first.", Color.Yellow);
                return;
            }

            try
            {
                File.WriteAllText(_files[_selectedFileIndex].FullPath, _editorText ?? "");
                _lastLoadedText = _editorText;
                _dirty = false;
                RebuildTextCache();
                _selectedFileLastWriteUtc = File.Exists(_files[_selectedFileIndex].FullPath)
                    ? File.GetLastWriteTimeUtc(_files[_selectedFileIndex].FullPath)
                    : DateTime.MinValue;
                SetStatus("Saved " + _files[_selectedFileIndex].DisplayPath, Color.LightGreen);
            }
            catch (Exception ex)
            {
                SetStatus("Save failed: " + ex.Message, Color.OrangeRed);
            }
        }

        private void ReloadAllFiles()
        {
            if (_dirty)
            {
                SetStatus("Save or reload current edits before reloading all files.", Color.Yellow);
                return;
            }

            RefreshFileList();
            SetStatus("Reloaded all config files from disk.", Color.LightGreen);
        }

        private void RefreshFileList()
        {
            string oldPath = (_selectedFileIndex >= 0 && _selectedFileIndex < _files.Count)
                ? _files[_selectedFileIndex].FullPath
                : null;

            var items = new List<ConfigFileItem>();
            string root = GetRuntimeModsRoot();

            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                foreach (var path in EnumerateConfigPaths(root))
                {
                    string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    items.Add(new ConfigFileItem
                    {
                        FullPath = path,
                        DisplayPath = relative
                    });
                }
            }

            _files.Clear();
            _files.AddRange(items.OrderBy(f => f.DisplayPath, StringComparer.OrdinalIgnoreCase));
            RebuildVisibleFileIndices(false);

            if (_visibleFileIndices.Count == 0)
            {
                _selectedFileIndex = -1;
                _editorText = "";
                _lastLoadedText = "";
                _caretIndex = 0;
                _dirty = false;
                RebuildTextCache();
                _selectedFileLastWriteUtc = DateTime.MinValue;
                SetStatus("No config files found under the live !Mods folder yet.", Color.Yellow);
                return;
            }

            int newIndex = _visibleFileIndices[0];
            if (!string.IsNullOrEmpty(oldPath))
            {
                int found = _files.FindIndex(f => string.Equals(f.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
                if (found >= 0 && _visibleFileIndices.Contains(found))
                    newIndex = found;
            }

            _selectedFileIndex = newIndex;
            LoadSelectedFile();
            _lastFileListFingerprint = ComputeFileListFingerprint(root);
        }

        private string GetRuntimeModsRoot()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string primary = Path.Combine(baseDir, "!Mods");
                if (Directory.Exists(primary))
                    return primary;
            }
            catch { }

            return null;
        }

        private void MarkDirty()
        {
            _dirty = !string.Equals(_editorText, _lastLoadedText, StringComparison.Ordinal);
            if (_dirty)
                SetStatus("Unsaved changes. Press Ctrl+S to save or Reload File to discard.", Color.Yellow);
        }

        private void InsertText(string text)
        {
            if (_selectedFileIndex < 0 || string.IsNullOrEmpty(text))
                return;

            if (_caretIndex < 0) _caretIndex = 0;
            if (_caretIndex > _editorText.Length) _caretIndex = _editorText.Length;

            _editorText = _editorText.Insert(_caretIndex, text);
            _caretIndex += text.Length;
            _preferredColumn = -1;
            RebuildTextCache();
            MarkDirty();
            EnsureCaretVisible();
        }

        private void MoveCaretToLineBoundary(bool start)
        {
            int line;
            int column;
            GetCaretLocation(out line, out column);

            if (line < 0 || line >= _lineStarts.Count)
                return;

            if (start)
            {
                _caretIndex = _lineStarts[line];
            }
            else
            {
                int lineEnd = GetLineEnd(line);
                _caretIndex = lineEnd;
            }

            _preferredColumn = -1;
            EnsureCaretVisible();
        }

        private void MoveCaretVertical(int delta)
        {
            int line;
            int column;
            GetCaretLocation(out line, out column);

            if (_lineStarts.Count == 0)
                return;

            if (_preferredColumn < 0)
                _preferredColumn = column;

            int targetLine = Math.Max(0, Math.Min(_lineStarts.Count - 1, line + delta));
            int targetStart = _lineStarts[targetLine];
            int targetEnd = GetLineEnd(targetLine);
            int targetColumn = Math.Min(_preferredColumn, targetEnd - targetStart);
            _caretIndex = targetStart + targetColumn;

            EnsureCaretVisible();
        }

        private void RebuildTextCache()
        {
            _cachedLines.Clear();
            _lineStarts.Clear();
            _lineStarts.Add(0);

            string text = _editorText ?? "";
            int lineStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int length = i - lineStart;
                    if (length > 0 && text[i - 1] == '\r')
                        length--;

                    _cachedLines.Add(text.Substring(lineStart, Math.Max(0, length)));
                    lineStart = i + 1;
                    _lineStarts.Add(lineStart);
                }
            }

            if (lineStart <= text.Length)
                _cachedLines.Add(text.Substring(lineStart));

            if (_cachedLines.Count == 0)
                _cachedLines.Add("");
        }

        private int GetLineEnd(int line)
        {
            int start = _lineStarts[line];
            int end = (line + 1 < _lineStarts.Count) ? _lineStarts[line + 1] - 1 : _editorText.Length;
            if (end > start && end - 1 >= 0 && end - 1 < _editorText.Length && _editorText[end - 1] == '\r')
                end--;
            return Math.Max(start, end);
        }

        private void GetCaretLocation(out int line, out int column)
        {
            line = 0;
            for (int i = 0; i < _lineStarts.Count; i++)
            {
                int start = _lineStarts[i];
                int next = (i + 1 < _lineStarts.Count) ? _lineStarts[i + 1] : _editorText.Length + 1;
                if (_caretIndex >= start && _caretIndex < next)
                {
                    line = i;
                    column = _caretIndex - start;
                    return;
                }
            }

            line = _lineStarts.Count - 1;
            column = Math.Max(0, _caretIndex - _lineStarts[line]);
        }

        private void EnsureCaretVisible()
        {
            int caretLine;
            int caretColumn;
            GetCaretLocation(out caretLine, out caretColumn);

            if (caretLine < _editorTopLine)
                _editorTopLine = caretLine;

            int visible = GetVisibleEditorLineCount();
            if (caretLine >= _editorTopLine + visible)
                _editorTopLine = caretLine - visible + 1;

            if (_editorTopLine < 0)
                _editorTopLine = 0;
        }

        private int GetVisibleEditorLineCount()
        {
            int headerHeight = _smallFont.LineSpacing + 12;
            int lineHeight = _smallFont.LineSpacing + 2;
            int contentTop = _editorRect.Top + headerHeight + 8;
            return Math.Max(1, (_editorRect.Bottom - contentTop - 8) / lineHeight);
        }

        private void EnsureSelectedFileVisible()
        {
            int visibleRows = GetVisibleFileRowCount();
            int selectedVisible = GetSelectedVisibleIndex();
            if (selectedVisible < 0)
                return;

            if (selectedVisible < _fileScroll)
                _fileScroll = selectedVisible;

            if (selectedVisible >= _fileScroll + visibleRows)
                _fileScroll = selectedVisible - visibleRows + 1;

            if (_fileScroll < 0)
                _fileScroll = 0;
        }

        private void ScrollFileListBy(int deltaRows)
        {
            int visibleRows = GetVisibleFileRowCount();
            int maxScroll = Math.Max(0, _visibleFileIndices.Count - visibleRows);

            _fileScroll += deltaRows;
            if (_fileScroll < 0)
                _fileScroll = 0;
            if (_fileScroll > maxScroll)
                _fileScroll = maxScroll;
        }

        private int GetFileRowHeight()
        {
            return _smallFont.LineSpacing + FileRowPadding * 2;
        }

        private int GetFileRowStep()
        {
            return GetFileRowHeight() + FileRowGap;
        }

        private int GetVisibleFileRowCount()
        {
            int availableHeight = _filesRect.Height - (GetFileRowHeight() + FileHeaderExtraHeight + FileContentTopGap + InputBoxHeight + FileContentTopGap) - 6;
            return Math.Max(1, availableHeight / GetFileRowStep());
        }

        private void UpdateFileScrollbarRects()
        {
            int rowHeight = GetFileRowHeight();
            int titleHeight = rowHeight + FileHeaderExtraHeight;
            int contentTop = _filesRect.Top + titleHeight + FileContentTopGap + InputBoxHeight + FileContentTopGap;
            int contentBottom = _filesRect.Bottom - 6;
            int trackHeight = Math.Max(8, contentBottom - contentTop);

            _fileScrollbarTrackRect = new Rectangle(
                _filesRect.Right - FileScrollbarMargin - FileScrollbarWidth,
                contentTop,
                FileScrollbarWidth,
                trackHeight);

            int visibleRows = GetVisibleFileRowCount();
            int totalRows = Math.Max(visibleRows, _visibleFileIndices.Count);
            _fileScrollbarVisible = _visibleFileIndices.Count > visibleRows;

            if (!_fileScrollbarVisible)
            {
                _fileScrollbarThumbRect = new Rectangle(_fileScrollbarTrackRect.X, _fileScrollbarTrackRect.Y, _fileScrollbarTrackRect.Width, _fileScrollbarTrackRect.Height);
                return;
            }

            float visibleRatio = (float)visibleRows / totalRows;
            int thumbHeight = Math.Max(FileScrollbarMinThumbHeight, (int)Math.Round(_fileScrollbarTrackRect.Height * visibleRatio));
            thumbHeight = Math.Min(thumbHeight, _fileScrollbarTrackRect.Height);

            int maxScroll = Math.Max(1, _visibleFileIndices.Count - visibleRows);
            float scrollT = MathHelper.Clamp((float)_fileScroll / maxScroll, 0f, 1f);
            int travel = Math.Max(0, _fileScrollbarTrackRect.Height - thumbHeight);
            int thumbY = _fileScrollbarTrackRect.Top + (int)Math.Round(travel * scrollT);

            _fileScrollbarThumbRect = new Rectangle(_fileScrollbarTrackRect.X, thumbY, _fileScrollbarTrackRect.Width, thumbHeight);
        }

        private void DrawFileScrollbar(SpriteBatch sb)
        {
            UpdateFileScrollbarRects();
            if (!_fileScrollbarVisible)
                return;

            sb.Draw(_white, _fileScrollbarTrackRect, new Color(18, 22, 29, 255));
            DrawBorder(sb, _fileScrollbarTrackRect, new Color(58, 68, 84, 255));

            var thumbColor = _draggingFileScrollbar
                ? new Color(136, 156, 192, 255)
                : new Color(92, 111, 146, 255);
            sb.Draw(_white, _fileScrollbarThumbRect, thumbColor);
            DrawBorder(sb, _fileScrollbarThumbRect, new Color(150, 168, 196, 255));
        }

        private bool HandleFileScrollbarClick(Point mousePoint)
        {
            UpdateFileScrollbarRects();
            if (!_fileScrollbarVisible)
                return false;

            if (_fileScrollbarThumbRect.Contains(mousePoint))
            {
                _draggingFileScrollbar = true;
                _fileScrollbarDragStartMouseY = mousePoint.Y;
                _fileScrollbarDragStartScroll = _fileScroll;
                return true;
            }

            if (_fileScrollbarTrackRect.Contains(mousePoint))
            {
                float clickT = (float)(mousePoint.Y - _fileScrollbarTrackRect.Top) / Math.Max(1, _fileScrollbarTrackRect.Height);
                JumpFileScrollbarTo(clickT);
                return true;
            }

            return false;
        }

        private void HandleFileScrollbar(MouseInput mouse, Point mousePoint)
        {
            if (!_draggingFileScrollbar)
                return;

            if (!mouse.LeftButtonDown)
            {
                _draggingFileScrollbar = false;
                return;
            }

            int visibleRows = GetVisibleFileRowCount();
            int maxScroll = Math.Max(0, _visibleFileIndices.Count - visibleRows);
            if (maxScroll <= 0)
                return;

            int travel = Math.Max(1, _fileScrollbarTrackRect.Height - _fileScrollbarThumbRect.Height);
            int dy = mousePoint.Y - _fileScrollbarDragStartMouseY;
            float deltaT = (float)dy / travel;
            _fileScroll = _fileScrollbarDragStartScroll + (int)Math.Round(deltaT * maxScroll);

            if (_fileScroll < 0)
                _fileScroll = 0;
            if (_fileScroll > maxScroll)
                _fileScroll = maxScroll;
        }

        private void JumpFileScrollbarTo(float scrollT)
        {
            int visibleRows = GetVisibleFileRowCount();
            int maxScroll = Math.Max(0, _visibleFileIndices.Count - visibleRows);

            scrollT = MathHelper.Clamp(scrollT, 0f, 1f);
            _fileScroll = (int)Math.Round(maxScroll * scrollT);
        }

        private void PlaceCaretFromMouse(Point mousePoint)
        {
            int headerHeight = _smallFont.LineSpacing + 12;
            int lineHeight = _smallFont.LineSpacing + 2;
            int contentTop = _editorRect.Top + headerHeight + 8;
            int lineNumberWidth = 56;

            int line = _editorTopLine + Math.Max(0, (mousePoint.Y - contentTop) / lineHeight);
            if (line >= _cachedLines.Count)
                line = _cachedLines.Count - 1;

            string currentLine = _cachedLines[line];
            int textStartX = _editorRect.Left + 8 + lineNumberWidth + 10;
            int targetX = Math.Max(0, mousePoint.X - textStartX);

            int column = 0;
            for (int i = 1; i <= currentLine.Length; i++)
            {
                float width = _smallFont.MeasureString(currentLine.Substring(0, i)).X;
                if (width >= targetX)
                {
                    column = i - 1;
                    break;
                }

                column = i;
            }

            if (line >= 0 && line < _lineStarts.Count)
            {
                _caretIndex = Math.Min(GetLineEnd(line), _lineStarts[line] + column);
                _preferredColumn = -1;
                EnsureCaretVisible();
            }
        }

        private void SetStatus(string message, Color color)
        {
            _statusMessage = message;
            _statusColor = color;
        }

        private void RebuildVisibleFileIndices(bool keepSelectionVisible)
        {
            _visibleFileIndices.Clear();
            string filter = (_fileFilterText ?? "").Trim();

            for (int i = 0; i < _files.Count; i++)
            {
                if (string.IsNullOrEmpty(filter) ||
                    _files[i].DisplayPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _visibleFileIndices.Add(i);
                }
            }

            if (_visibleFileIndices.Count == 0)
            {
                _selectedFileIndex = -1;
                _fileScroll = 0;
                return;
            }

            if (_selectedFileIndex < 0 || !_visibleFileIndices.Contains(_selectedFileIndex))
            {
                _selectedFileIndex = _visibleFileIndices[0];
                if (keepSelectionVisible)
                    LoadSelectedFile();
            }

            EnsureSelectedFileVisible();
        }

        private void DrawInputBox(SpriteBatch sb, Rectangle rect, string value, string placeholder, bool focused)
        {
            sb.Draw(_white, rect, new Color(22, 28, 36, 255));
            DrawBorder(sb, rect, focused ? new Color(130, 170, 230, 255) : new Color(72, 84, 104, 255));

            string text = string.IsNullOrEmpty(value) ? placeholder : value;
            Color color = string.IsNullOrEmpty(value) ? new Color(140, 150, 165, 255) : Color.White;

            string clipped = ClipText(_smallFont, text, rect.Width - 16);
            Vector2 pos = new Vector2(rect.Left + 8, rect.Top + 6);
            sb.DrawString(_smallFont, clipped, pos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, clipped, pos, color);
        }

        private void PollHotReload()
        {
            int now = Environment.TickCount;
            uint dt = unchecked((uint)(now - _lastHotReloadPollTick));
            if (dt < HotReloadPollMs)
                return;

            _lastHotReloadPollTick = now;

            string root = GetRuntimeModsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            if (_dirty)
                return;

            try
            {
                ulong fingerprint = ComputeFileListFingerprint(root);
                if (fingerprint == _lastFileListFingerprint)
                    return;

                RefreshFileList();
                SetStatus("Hot-reload: detected config changes and reloaded all files.", Color.LightGreen);
            }
            catch { }
        }

        private static ulong ComputeFileListFingerprint(string root)
        {
            // Lightweight deterministic snapshot of relevant config files.
            ulong hash = 1469598103934665603UL; // FNV-1a offset basis

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return hash;

            var files = EnumerateConfigPaths(root).ToArray();
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                var info = new FileInfo(path);
                string stamp = path + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                for (int c = 0; c < stamp.Length; c++)
                {
                    hash ^= stamp[c];
                    hash *= 1099511628211UL; // FNV prime
                }
            }

            return hash;
        }

        private static IEnumerable<string> EnumerateConfigPaths(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                yield break;

            var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".ini", ".cfg", ".config", ".json", ".xml"
            };

            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "!Logs",
                "!NetLogs",
                "TexturePacks",
                "_FbxToXnb",
                "obj",
                "bin",
                ".git"
            };

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] childDirs;
                try
                {
                    childDirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    childDirs = null;
                }

                if (childDirs != null)
                {
                    for (int d = 0; d < childDirs.Length; d++)
                    {
                        string name = Path.GetFileName(childDirs[d]);
                        if (skipDirs.Contains(name))
                            continue;
                        stack.Push(childDirs[d]);
                    }
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    files = null;
                }

                if (files == null)
                    continue;

                for (int f = 0; f < files.Length; f++)
                {
                    string ext = Path.GetExtension(files[f]);
                    if (allowedExts.Contains(ext))
                        yield return files[f];
                }
            }
        }

        private static string ClipText(SpriteFont font, string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || font.MeasureString(text).X <= maxWidth)
                return text ?? "";

            const string ellipsis = "...";
            while (text.Length > 0 && font.MeasureString(text + ellipsis).X > maxWidth)
                text = text.Substring(0, text.Length - 1);

            return text + ellipsis;
        }

        private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
        {
            sb.Draw(_white, new Rectangle(rect.Left, rect.Top, rect.Width, 1), color);
            sb.Draw(_white, new Rectangle(rect.Left, rect.Bottom - 1, rect.Width, 1), color);
            sb.Draw(_white, new Rectangle(rect.Left, rect.Top, 1, rect.Height), color);
            sb.Draw(_white, new Rectangle(rect.Right - 1, rect.Top, 1, rect.Height), color);
        }

        private void DrawCenteredString(SpriteBatch sb, SpriteFont font, string text, Rectangle rect, Color color)
        {
            Vector2 size = font.MeasureString(text);
            Vector2 pos = new Vector2(
                rect.Left + (rect.Width - size.X) * 0.5f,
                rect.Top + (rect.Height - size.Y) * 0.5f);

            pos = new Vector2((float)Math.Round(pos.X), (float)Math.Round(pos.Y));

            sb.DrawString(font, text, pos + new Vector2(1, 1), Color.Black);
            sb.DrawString(font, text, pos, color);
        }
    }
}

