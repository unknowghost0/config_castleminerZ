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
using System.Threading;
using System.Reflection;
using System;
using static ModLoader.LogSystem;

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
        private Rectangle _reloadFileRect;
        private Rectangle _reloadRect;
        private Rectangle _validateRect;
        private Rectangle _restoreBakRect;
        private Rectangle _refreshRect;
        private Rectangle _closeRect;
        private Rectangle _fileScrollbarTrackRect;
        private Rectangle _fileScrollbarThumbRect;
        private Rectangle _fileFilterRect;
        private Rectangle _editorSearchRect;

        private readonly List<ConfigFileItem> _files = new List<ConfigFileItem>();
        private readonly List<int> _visibleFileIndices = new List<int>();
        private readonly List<FileListRow> _visibleRows = new List<FileListRow>();
        private readonly List<GroupBucket> _groupBuckets = new List<GroupBucket>();
        private readonly HashSet<string> _expandedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentPaths = new List<string>();
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
        private DateTime _selectedFileLastWriteUtc = DateTime.MinValue;
        private string _fileFilterText = "";
        private bool _focusFileFilter;
        private string _editorSearchText = "";
        private bool _focusEditorSearch;
        private readonly List<int> _searchMatchIndices = new List<int>();
        private int _activeSearchMatch = -1;
        private bool _showRecentTab;
        private Rectangle _filesTabRect;
        private Rectangle _recentTabRect;
        private string _lastValidationMessage = "";
        private Color _lastValidationColor = Color.LightGray;
        private int _lastDiffChanged;
        private int _lastDiffAdded;
        private int _lastDiffRemoved;

        private sealed class UndoState
        {
            public string Text = "";
            public int Caret;
            public int TopLine;
        }
        private sealed class FileListRow
        {
            public bool IsGroup;
            public string GroupKey;
            public string GroupLabel;
            public int FileIndex = -1;
        }
        private sealed class GroupBucket
        {
            public string Key;
            public string Label;
            public readonly List<int> FileIndices = new List<int>();
        }
        private sealed class UndoHistory
        {
            public readonly Stack<UndoState> Undo = new Stack<UndoState>();
            public readonly Stack<UndoState> Redo = new Stack<UndoState>();
        }
        private readonly Dictionary<string, UndoHistory> _undoByPath = new Dictionary<string, UndoHistory>(StringComparer.OrdinalIgnoreCase);
        private readonly object _scanLock = new object();
        private bool _scanInProgress;
        private bool _hasPendingScanResult;
        private List<ConfigFileItem> _pendingScanItems;
        private string _pendingOldPath;
        private bool _firstDrawTraced;
        private bool _firstInputTraced;

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
        private const int MaxEnumeratedDirectories = 5000;
        private const int PaneGutter = 10;
        private const int MaxRecentFiles = 24;
        private const int MaxUndoStatesPerFile = 100;
        private static bool HideLeftHeaderAndFilter = false;
        private static bool HideLeftFileListContent = false;

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
            DebugTrace("ConfigScreen pushed.");
            BeginRefreshFileList();
        }

        public override void OnPoped()
        {
            base.OnPoped();
            IsOpen = false;
            CaptureMouse = false;
            _undoByPath.Clear();
            DebugTrace("ConfigScreen popped.");
        }

        protected override void OnDraw(GraphicsDevice device, SpriteBatch spriteBatch, GameTime gameTime)
        {
            try
            {
                EnsureInit(device);
                Layout(device);
                ApplyPendingScanResult();
                PollHotReload();

                if (!_firstDrawTraced)
                {
                    _firstDrawTraced = true;
                    DebugTrace("OnDraw first frame reached.");
                }

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
            catch (Exception ex)
            {
                DebugTrace("OnDraw exception: " + ex.GetType().Name + ": " + ex.Message);
                SetStatus("Config draw error: " + ex.Message, Color.OrangeRed);
            }
        }

        protected override bool OnChar(GameTime gameTime, char c)
        {
            if (!HideLeftHeaderAndFilter && _focusFileFilter)
            {
                if (!char.IsControl(c))
                {
                    _fileFilterText += c;
                    DebugTrace($"Filter char '{c}' -> \"{_fileFilterText}\"");
                    RebuildVisibleFileIndices(true);
                }

                return false;
            }

            if (_focusEditorSearch)
            {
                if (!char.IsControl(c))
                {
                    _editorSearchText += c;
                    RebuildSearchMatches();
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

            if (!_firstInputTraced)
            {
                _firstInputTraced = true;
                DebugTrace("OnPlayerInput first call reached.");
            }

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

            if (ctrlDown && input.Keyboard.WasKeyPressed(Keys.Z))
            {
                UndoEdit();
                return false;
            }

            if (ctrlDown && input.Keyboard.WasKeyPressed(Keys.Y))
            {
                RedoEdit();
                return false;
            }

            if (ctrlDown && input.Keyboard.WasKeyPressed(Keys.F))
            {
                _focusEditor = false;
                _focusFileFilter = false;
                _focusEditorSearch = true;
                return false;
            }

            if (input.Keyboard.WasKeyPressed(Keys.F5))
            {
                BeginRefreshFileList();
                return false;
            }

            if (input.Keyboard.WasKeyPressed(Keys.F3) || (_focusEditorSearch && input.Keyboard.WasKeyPressed(Keys.Enter)))
            {
                JumpToNextSearchMatch();
                return false;
            }

            if (input.Mouse.DeltaWheel != 0 && _filesRect.Contains(mousePoint))
            {
                if (HideLeftFileListContent)
                    return false;

                _focusEditor = false;
                ScrollFileListBy(-Math.Sign(input.Mouse.DeltaWheel) * 3);
                return false;
            }

            if (!HideLeftHeaderAndFilter && _focusFileFilter)
            {
                if (input.Keyboard.WasKeyPressed(Keys.Back) && _fileFilterText.Length > 0)
                {
                    _fileFilterText = _fileFilterText.Substring(0, _fileFilterText.Length - 1);
                    DebugTrace($"Filter backspace -> \"{_fileFilterText}\"");
                    RebuildVisibleFileIndices(true);
                    return false;
                }

                if (input.Keyboard.WasKeyPressed(Keys.Delete) && _fileFilterText.Length > 0)
                {
                    _fileFilterText = "";
                    DebugTrace("Filter cleared.");
                    RebuildVisibleFileIndices(true);
                    return false;
                }
            }

            if (_focusEditorSearch)
            {
                if (input.Keyboard.WasKeyPressed(Keys.Back) && _editorSearchText.Length > 0)
                {
                    _editorSearchText = _editorSearchText.Substring(0, _editorSearchText.Length - 1);
                    RebuildSearchMatches();
                    return false;
                }

                if (input.Keyboard.WasKeyPressed(Keys.Delete) && _editorSearchText.Length > 0)
                {
                    _editorSearchText = "";
                    RebuildSearchMatches();
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

            return base.OnPlayerInput(input, controller, chatpad, gameTime);
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
            _editorRect = new Rectangle(_filesRect.Right + PanePadding + PaneGutter, contentTop, _panelRect.Right - _filesRect.Right - PanePadding * 2 - PaneGutter, contentHeight);
            _statusRect = new Rectangle(_panelRect.Left + PanePadding, contentTop + contentHeight + 8, _panelRect.Width - PanePadding * 2, statusHeight);
            _fileFilterRect = new Rectangle(_filesRect.Left + 8, _filesRect.Top + (GetFileRowHeight() + FileHeaderExtraHeight) + 6, _filesRect.Width - 16, InputBoxHeight);
            _filesTabRect = new Rectangle(_filesRect.Left + 8, _filesRect.Top + 2, (_filesRect.Width - 20) / 2, GetFileRowHeight());
            _recentTabRect = new Rectangle(_filesTabRect.Right + 4, _filesRect.Top + 2, (_filesRect.Width - 20) - _filesTabRect.Width - 4, GetFileRowHeight());
            int editorSearchW = Math.Max(220, Math.Min(420, (int)(_editorRect.Width * 0.42f)));
            int editorSearchH = Math.Max(22, _smallFont.LineSpacing + 8);
            _editorSearchRect = new Rectangle(_editorRect.Right - editorSearchW - 8, _editorRect.Top + 4, editorSearchW, editorSearchH);
            int closeX = _panelRect.Right - PanePadding - ButtonWidth;
            _closeRect = new Rectangle(closeX, buttonsY, ButtonWidth, ButtonHeight);
            _refreshRect = new Rectangle(_closeRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _restoreBakRect = new Rectangle(_refreshRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _validateRect = new Rectangle(_restoreBakRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _reloadRect = new Rectangle(_validateRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _reloadFileRect = new Rectangle(_reloadRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
            _saveRect = new Rectangle(_reloadFileRect.Left - ButtonGap - ButtonWidth, buttonsY, ButtonWidth, ButtonHeight);
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
            // Hard visual gutter prevents any cross-pane text bleed from looking like overlap.
            var gutterRect = new Rectangle(_filesRect.Right + PanePadding, _filesRect.Top, PaneGutter, _filesRect.Height);
            sb.Draw(_white, gutterRect, bg);
            sb.Draw(_white, _statusRect, new Color(20, 24, 31, 255));
            DrawBorder(sb, _filesRect, new Color(62, 75, 95, 255));
            DrawBorder(sb, _editorRect, new Color(62, 75, 95, 255));
            DrawBorder(sb, _statusRect, new Color(62, 75, 95, 255));
        }

        private void DrawTitle(SpriteBatch sb)
        {
            var titlePos = new Vector2(_panelRect.Left + PanePadding, _panelRect.Top + PanePadding - 2);

            sb.DrawString(_titleFont, "Config v2", titlePos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_titleFont, "Config v2", titlePos, Color.White);
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
            DrawButton(sb, _reloadFileRect, "RELOAD FILE", new Color(58, 65, 80, 255));
            DrawButton(sb, _reloadRect, "RELOAD ALL", new Color(58, 65, 80, 255));
            DrawButton(sb, _validateRect, "VALIDATE", new Color(58, 65, 80, 255));
            DrawButton(sb, _restoreBakRect, "RESTORE BAK", new Color(58, 65, 80, 255));
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
            if (HideLeftFileListContent)
                return;

            int rowHeight = GetFileRowHeight();
            int visibleRows = GetVisibleFileRowCount();
            int maxScroll = Math.Max(0, _visibleRows.Count - visibleRows);
            if (_fileScroll > maxScroll) _fileScroll = maxScroll;

            if (!HideLeftHeaderAndFilter)
            {
                DrawButton(sb, _filesTabRect, "Config Files", !_showRecentTab ? new Color(56, 78, 118, 255) : new Color(34, 42, 54, 255));
                DrawButton(sb, _recentTabRect, "Recent", _showRecentTab ? new Color(56, 78, 118, 255) : new Color(34, 42, 54, 255));
                DrawInputBox(sb, _fileFilterRect, _fileFilterText, "Find file...", _focusFileFilter);
            }

            int y = GetFilesContentTop();
            int clipBottom = _filesRect.Bottom - 6;
            int textRightPadding = _fileScrollbarVisible ? (FileScrollbarWidth + FileScrollbarMargin * 3) : 16;
            for (int row = 0; row < visibleRows; row++)
            {
                int rowIndex = _fileScroll + row;
                if (rowIndex >= _visibleRows.Count)
                    break;
                var rowData = _visibleRows[rowIndex];

                var itemRect = new Rectangle(_filesRect.Left + 6, y, _filesRect.Width - 12, rowHeight);
                if (itemRect.Bottom > clipBottom)
                    break;

                if (rowData.IsGroup)
                {
                    bool expanded = _expandedGroups.Contains(rowData.GroupKey);
                    sb.Draw(_white, itemRect, new Color(40, 52, 72, 255));
                    string marker = expanded ? "[-] " : "[+] ";
                    string label = marker + (rowData.GroupLabel ?? "Unknown");
                    var gp = new Vector2(itemRect.Left + 8, itemRect.Top + FileRowPadding);
                    string gtext = ClipText(_smallFont, label, itemRect.Width - textRightPadding);
                    sb.DrawString(_smallFont, gtext, gp + new Vector2(1, 1), Color.Black);
                    sb.DrawString(_smallFont, gtext, gp, new Color(225, 235, 255, 255));
                }
                else
                {
                    int index = rowData.FileIndex;
                    bool selected = index == _selectedFileIndex;
                    var fill = selected ? new Color(70, 98, 145, 255) : new Color(30, 36, 45, 255);
                    sb.Draw(_white, itemRect, fill);

                    var pos = new Vector2(itemRect.Left + 20, itemRect.Top + FileRowPadding);
                    string text = ClipText(_smallFont, _files[index].DisplayPath, itemRect.Width - textRightPadding - 12);
                    sb.DrawString(_smallFont, text, pos + new Vector2(1, 1), Color.Black);
                    sb.DrawString(_smallFont, text, pos, selected ? Color.White : new Color(210, 214, 220, 255));
                }

                y += GetFileRowStep();
            }

            DrawFileScrollbar(sb);

            if (_visibleRows.Count == 0)
            {
                var msg = _files.Count == 0 ? "No config files were found under !Mods yet." : "No files match the filter.";
                if (_showRecentTab) msg = "No recent files yet.";
                var pos = new Vector2(_filesRect.Left + 12, y + 8);
                sb.DrawString(_smallFont, msg, pos + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, msg, pos, Color.LightGray);
            }
        }

        private void DrawEditor(SpriteBatch sb)
        {
            int headerHeight = Math.Max(_smallFont.LineSpacing + 12, _editorSearchRect.Height + 8);
            var headerRect = new Rectangle(_editorRect.Left, _editorRect.Top, _editorRect.Width, headerHeight);
            sb.Draw(_white, headerRect, new Color(34, 42, 54, 255));

            string title = "Editor";
            if (_selectedFileIndex >= 0)
            {
                string display = _files[_selectedFileIndex].DisplayPath ?? "";
                string fileName = Path.GetFileName(display);
                title = string.IsNullOrWhiteSpace(fileName) ? display : fileName;
            }
            int titleLeftPad = 64;
            int titleMaxWidth = Math.Max(100, _editorSearchRect.Left - (_editorRect.Left + titleLeftPad) - 12);
            string titleClipped = ClipText(_smallFont, title, titleMaxWidth);
            var titlePos = new Vector2(_editorRect.Left + titleLeftPad, _editorRect.Top + 6);
            sb.DrawString(_smallFont, titleClipped, titlePos + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, titleClipped, titlePos, Color.White);
            DrawInputBox(sb, _editorSearchRect, _editorSearchText, "Find in file... (Enter/F3 = next)", _focusEditorSearch);

            int lineHeight = _smallFont.LineSpacing + 2;
            int contentTop = headerRect.Bottom + 8;
            int diffPanelHeight = Math.Max(90, _smallFont.LineSpacing * 5 + 14);
            int editorBottom = _editorRect.Bottom - diffPanelHeight - 6;
            int visibleLines = Math.Max(1, (editorBottom - contentTop - 8) / lineHeight);

            if (_editorTopLine > Math.Max(0, _cachedLines.Count - visibleLines))
                _editorTopLine = Math.Max(0, _cachedLines.Count - visibleLines);

            int lineNumberWidth = 56;
            var lineRect = new Rectangle(_editorRect.Left + 8, contentTop, _editorRect.Width - 16, editorBottom - contentTop);
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
                string lineText = _cachedLines[lineIndex];
                DrawLineSearchHighlights(sb, lineText, lineIndex, textPos, lineRect.Width - lineNumberWidth - 20);
                DrawSyntaxLine(sb, lineText, textPos, lineRect.Width - lineNumberWidth - 20);
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

            var diffRect = new Rectangle(_editorRect.Left + 8, _editorRect.Bottom - diffPanelHeight, _editorRect.Width - 16, diffPanelHeight - 8);
            DrawDiffPanel(sb, diffRect);
        }

        private void DrawDiffPanel(SpriteBatch sb, Rectangle rect)
        {
            sb.Draw(_white, rect, new Color(21, 25, 33, 255));
            DrawBorder(sb, rect, new Color(62, 75, 95, 255));

            UpdateDiffStats();
            string headline = "Diff  changed:" + _lastDiffChanged.ToString(CultureInfo.InvariantCulture)
                            + "  added:" + _lastDiffAdded.ToString(CultureInfo.InvariantCulture)
                            + "  removed:" + _lastDiffRemoved.ToString(CultureInfo.InvariantCulture);
            var p = new Vector2(rect.Left + 8, rect.Top + 6);
            sb.DrawString(_smallFont, headline, p + new Vector2(1, 1), Color.Black);
            sb.DrawString(_smallFont, headline, p, Color.White);

            if (!string.IsNullOrEmpty(_lastValidationMessage))
            {
                var pv = new Vector2(rect.Left + 8, p.Y + _smallFont.LineSpacing + 2);
                sb.DrawString(_smallFont, _lastValidationMessage, pv + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, _lastValidationMessage, pv, _lastValidationColor);
            }

            string[] oldLines = (_lastLoadedText ?? "").Replace("\r\n", "\n").Split('\n');
            string[] newLines = (_editorText ?? "").Replace("\r\n", "\n").Split('\n');
            int row = 0;
            int maxPreview = 3;
            int previewTop = rect.Top + _smallFont.LineSpacing * 2 + 10;
            int previewWidth = rect.Width - 16;
            int max = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < max && row < maxPreview; i++)
            {
                string o = i < oldLines.Length ? oldLines[i] : null;
                string n = i < newLines.Length ? newLines[i] : null;
                if (o == n)
                    continue;

                string label;
                Color c;
                if (o == null) { label = "+ " + n; c = new Color(140, 220, 140, 255); }
                else if (n == null) { label = "- " + o; c = new Color(235, 140, 140, 255); }
                else { label = "~ " + n; c = new Color(235, 208, 120, 255); }

                label = ClipText(_smallFont, label, previewWidth);
                var lp = new Vector2(rect.Left + 8, previewTop + row * (_smallFont.LineSpacing + 2));
                sb.DrawString(_smallFont, label, lp + new Vector2(1, 1), Color.Black);
                sb.DrawString(_smallFont, label, lp, c);
                row++;
            }
        }

        private void UpdateDiffStats()
        {
            string[] oldLines = (_lastLoadedText ?? "").Replace("\r\n", "\n").Split('\n');
            string[] newLines = (_editorText ?? "").Replace("\r\n", "\n").Split('\n');
            int max = Math.Max(oldLines.Length, newLines.Length);
            int changed = 0;
            int added = 0;
            int removed = 0;
            for (int i = 0; i < max; i++)
            {
                string o = i < oldLines.Length ? oldLines[i] : null;
                string n = i < newLines.Length ? newLines[i] : null;
                if (o == n) continue;
                if (o == null) added++;
                else if (n == null) removed++;
                else changed++;
            }
            _lastDiffChanged = changed;
            _lastDiffAdded = added;
            _lastDiffRemoved = removed;
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

            if (_reloadFileRect.Contains(mousePoint))
            {
                ReloadSelectedFile();
                return;
            }

            if (_reloadRect.Contains(mousePoint))
            {
                ReloadAllFiles();
                return;
            }

            if (_validateRect.Contains(mousePoint))
            {
                ValidateSelectedFile();
                return;
            }

            if (_restoreBakRect.Contains(mousePoint))
            {
                RestoreSelectedBak();
                return;
            }

            if (_refreshRect.Contains(mousePoint))
            {
                BeginRefreshFileList();
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

            if (!HideLeftHeaderAndFilter && _fileFilterRect.Contains(mousePoint))
            {
                _focusEditor = false;
                _focusFileFilter = true;
                _focusEditorSearch = false;
                DebugTrace("Filter box focused.");
                return;
            }

            if (!HideLeftHeaderAndFilter && _filesTabRect.Contains(mousePoint))
            {
                _showRecentTab = false;
                _fileScroll = 0;
                RebuildVisibleFileIndices(true);
                return;
            }

            if (!HideLeftHeaderAndFilter && _recentTabRect.Contains(mousePoint))
            {
                _showRecentTab = true;
                _fileScroll = 0;
                RebuildVisibleFileIndices(true);
                return;
            }

            if (_editorSearchRect.Contains(mousePoint))
            {
                _focusEditor = false;
                _focusFileFilter = false;
                _focusEditorSearch = true;
                return;
            }

            if (_filesRect.Contains(mousePoint))
            {
                if (HideLeftFileListContent)
                    return;

                _focusEditor = false;
                _focusFileFilter = false;
                _focusEditorSearch = false;
                DebugTrace($"Files pane click at ({mousePoint.X},{mousePoint.Y}), scroll={_fileScroll}, rows={_visibleRows.Count}, files={_visibleFileIndices.Count}");
                ClickFileList(mousePoint);
                return;
            }

            if (_editorRect.Contains(mousePoint) && _selectedFileIndex >= 0)
            {
                _focusEditor = true;
                _focusFileFilter = false;
                _focusEditorSearch = false;
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
                    PushUndoBeforeEdit();
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
                    PushUndoBeforeEdit();
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
            int headerBottom = GetFilesContentTop();
            if (mousePoint.Y < headerBottom)
                return;

            int row = (mousePoint.Y - headerBottom) / GetFileRowStep();
            int rowIndex = _fileScroll + row;
            DebugTrace($"ClickFileList row={row}, fileScroll={_fileScroll}, targetRowIndex={rowIndex}");
            if (rowIndex < 0 || rowIndex >= _visibleRows.Count)
                return;

            var rowData = _visibleRows[rowIndex];
            if (rowData.IsGroup)
            {
                ToggleGroupExpanded(rowData.GroupKey);
                return;
            }

            TrySelectVisibleFileByRowIndex(rowIndex);
        }

        private void TrySelectVisibleFile(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= _visibleFileIndices.Count)
            {
                DebugTrace($"TrySelectVisibleFile ignored (index={visibleIndex}, visibleCount={_visibleFileIndices.Count})");
                return;
            }

            DebugTrace($"TrySelectVisibleFile visibleIndex={visibleIndex} -> fileIndex={_visibleFileIndices[visibleIndex]}");
            TrySelectFile(_visibleFileIndices[visibleIndex]);
        }

        private void TrySelectVisibleFileByRowIndex(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _visibleRows.Count)
                return;

            var rowData = _visibleRows[rowIndex];
            if (rowData.IsGroup || rowData.FileIndex < 0)
                return;

            TrySelectFile(rowData.FileIndex);
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
            {
                DebugTrace($"TrySelectFile ignored (index={index}, fileCount={_files.Count})");
                return;
            }

            if (_dirty && index != _selectedFileIndex)
            {
                DebugTrace($"TrySelectFile blocked by dirty state (current={_selectedFileIndex}, requested={index})");
                SetStatus("Save or reload the current file before switching files.", Color.Yellow);
                return;
            }

            _selectedFileIndex = index;
            DebugTrace($"TrySelectFile selected index={index}, path={_files[index].DisplayPath}");
            AddRecentPath(_files[index].FullPath);
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
                RebuildSearchMatches();
                _selectedFileLastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                DebugTrace($"LoadSelectedFile success path={_files[_selectedFileIndex].DisplayPath}, chars={_editorText.Length}");
                SetStatus("Loaded " + _files[_selectedFileIndex].DisplayPath, Color.LightGreen);
            }
            catch (Exception ex)
            {
                _editorText = "";
                _lastLoadedText = "";
                _caretIndex = 0;
                _dirty = false;
                RebuildTextCache();
                RebuildSearchMatches();
                _selectedFileLastWriteUtc = DateTime.MinValue;
                DebugTrace($"LoadSelectedFile failed: {ex.Message}");
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
                string path = _files[_selectedFileIndex].FullPath;
                WriteBackupsBeforeSave(path);
                File.WriteAllText(path, _editorText ?? "");
                _lastLoadedText = _editorText;
                _dirty = false;
                RebuildTextCache();
                _selectedFileLastWriteUtc = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.MinValue;
                SetStatus("Saved " + _files[_selectedFileIndex].DisplayPath + " (+ .bak + history)", Color.LightGreen);
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

            BeginRefreshFileList();
            SetStatus("Reloading all config files from disk...", Color.LightGreen);
        }

        private void ReloadSelectedFile()
        {
            if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
            {
                SetStatus("Pick a config file first.", Color.Yellow);
                return;
            }

            LoadSelectedFile();

            string hotReloadMessage;
            bool hotReloaded = TryHotReloadSelectedFile(out hotReloadMessage);
            if (hotReloaded)
            {
                SetStatus("Reloaded + hot-reloaded " + _files[_selectedFileIndex].DisplayPath + " (" + hotReloadMessage + ")", Color.LightGreen);
            }
            else
            {
                SetStatus("Reloaded " + _files[_selectedFileIndex].DisplayPath + " (no hot-reload hook found)", Color.Yellow);
            }
        }

        private bool TryHotReloadSelectedFile(out string detail)
        {
            detail = "";
            try
            {
                if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
                {
                    detail = "no file selected";
                    return false;
                }

                string displayPath = _files[_selectedFileIndex].DisplayPath ?? "";
                string modToken = GetModTokenFromDisplayPath(displayPath);
                if (string.IsNullOrWhiteSpace(modToken))
                {
                    detail = "could not determine mod name";
                    return false;
                }

                string wanted = NormalizeToken(modToken);
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                if (loadedAssemblies == null || loadedAssemblies.Length == 0)
                {
                    detail = "no loaded assemblies";
                    return false;
                }

                Assembly bestAssembly = null;
                int bestScore = int.MinValue;
                for (int i = 0; i < loadedAssemblies.Length; i++)
                {
                    var asm = loadedAssemblies[i];
                    if (asm == null || asm.IsDynamic) continue;

                    string asmName = "";
                    try { asmName = asm.GetName().Name ?? ""; } catch { }
                    string normAsm = NormalizeToken(asmName);
                    if (string.IsNullOrEmpty(normAsm)) continue;

                    int score = 0;
                    if (normAsm == wanted) score = 1000;
                    else if (normAsm.Contains(wanted) || wanted.Contains(normAsm)) score = 600;
                    else continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAssembly = asm;
                    }
                }

                if (bestAssembly == null)
                {
                    detail = "assembly not found for " + modToken;
                    return false;
                }

                MethodInfo bestMethod = null;
                object[] bestArgs = null;
                int bestMethodScore = int.MinValue;
                Type[] types;
                try { types = bestAssembly.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                catch { types = null; }

                if (types == null || types.Length == 0)
                {
                    detail = "no types in " + bestAssembly.GetName().Name;
                    return false;
                }

                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type == null) continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int m = 0; m < methods.Length; m++)
                    {
                        var method = methods[m];
                        if (method == null) continue;
                        var parameters = method.GetParameters();
                        bool supportsNoArgs = parameters.Length == 0;
                        bool supportsPathArg = parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                        if (!supportsNoArgs && !supportsPathArg) continue;

                        string name = method.Name ?? "";
                        int nameScore = GetHotReloadMethodNameScore(name);
                        if (nameScore <= 0) continue;

                        int score = 0;
                        score += nameScore;
                        if (supportsNoArgs) score += 45;
                        if (supportsPathArg) score += 35;

                        string typeName = type.Name ?? "";
                        if (typeName.IndexOf("Config", StringComparison.OrdinalIgnoreCase) >= 0) score += 120;
                        if (typeName.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0) score += 80;
                        if (typeName.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0) score += 30;
                        if (typeName.IndexOf(modToken, StringComparison.OrdinalIgnoreCase) >= 0) score += 40;

                        string ns = type.Namespace ?? "";
                        if (ns.StartsWith(bestAssembly.GetName().Name ?? "", StringComparison.OrdinalIgnoreCase)) score += 25;

                        if (score > bestMethodScore)
                        {
                            bestMethodScore = score;
                            bestMethod = method;
                            bestArgs = supportsPathArg
                                ? new object[] { _files[_selectedFileIndex].FullPath }
                                : null;
                        }
                    }
                }

                if (bestMethod == null)
                {
                    detail = "no supported hot-reload hook in " + bestAssembly.GetName().Name;
                    return false;
                }

                bestMethod.Invoke(null, bestArgs);
                detail = bestMethod.DeclaringType.FullName + "." + bestMethod.Name + "()";
                DebugTrace("Hot-reload invoked: " + detail + " for " + displayPath);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                DebugTrace("Hot-reload failed: " + detail);
                return false;
            }
        }

        private static string GetModTokenFromDisplayPath(string displayPath)
        {
            if (string.IsNullOrWhiteSpace(displayPath))
                return "";

            char[] sep = new[] { '\\', '/' };
            var parts = displayPath.Split(sep, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts[0];

            return Path.GetFileNameWithoutExtension(displayPath) ?? "";
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        private static int GetHotReloadMethodNameScore(string methodName)
        {
            if (string.IsNullOrWhiteSpace(methodName))
                return 0;

            if (string.Equals(methodName, "LoadApply", StringComparison.Ordinal))
                return 1100;
            if (string.Equals(methodName, "Reload", StringComparison.Ordinal))
                return 1000;
            if (string.Equals(methodName, "ReloadConfig", StringComparison.Ordinal))
                return 980;
            if (string.Equals(methodName, "LoadConfig", StringComparison.Ordinal))
                return 940;
            if (string.Equals(methodName, "ApplyConfig", StringComparison.Ordinal))
                return 900;
            if (string.Equals(methodName, "Apply", StringComparison.Ordinal))
                return 820;
            if (string.Equals(methodName, "Refresh", StringComparison.Ordinal))
                return 760;
            if (string.Equals(methodName, "OnConfigChanged", StringComparison.Ordinal))
                return 740;

            return 0;
        }

        private void BeginRefreshFileList()
        {
            if (_scanInProgress)
            {
                SetStatus("Config scan already in progress...", Color.LightGray);
                return;
            }

            string oldPath = (_selectedFileIndex >= 0 && _selectedFileIndex < _files.Count)
                ? _files[_selectedFileIndex].FullPath
                : null;

            _scanInProgress = true;
            SetStatus("Scanning config files...", Color.LightGray);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var items = new List<ConfigFileItem>();
                string root = GetRuntimeModsRoot();

                try
                {
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
                }
                catch (Exception ex)
                {
                    DebugTrace("Background scan failed: " + ex.Message);
                }

                lock (_scanLock)
                {
                    _pendingScanItems = items.OrderBy(f => f.DisplayPath, StringComparer.OrdinalIgnoreCase).ToList();
                    _pendingOldPath = oldPath;
                    _hasPendingScanResult = true;
                    _scanInProgress = false;
                }
            });
        }

        private void ApplyPendingScanResult()
        {
            List<ConfigFileItem> items = null;
            string oldPath = null;

            lock (_scanLock)
            {
                if (!_hasPendingScanResult)
                    return;

                items = _pendingScanItems ?? new List<ConfigFileItem>();
                oldPath = _pendingOldPath;
                _pendingScanItems = null;
                _pendingOldPath = null;
                _hasPendingScanResult = false;
            }

            string root = GetRuntimeModsRoot();
            _files.Clear();
            _files.AddRange(items);
            BuildGroupBuckets();
            RebuildVisibleFileIndices(false);
            DebugTrace($"ApplyPendingScanResult found {_files.Count} files under root={root}");

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

            PushUndoBeforeEdit();
            _editorText = _editorText.Insert(_caretIndex, text);
            _caretIndex += text.Length;
            _preferredColumn = -1;
            RebuildTextCache();
            MarkDirty();
            EnsureCaretVisible();
        }

        private string GetSelectedPathOrNull()
        {
            if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
                return null;
            return _files[_selectedFileIndex].FullPath;
        }

        private UndoHistory GetUndoHistory(string path, bool create)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            UndoHistory h;
            if (_undoByPath.TryGetValue(path, out h))
                return h;

            if (!create)
                return null;

            h = new UndoHistory();
            _undoByPath[path] = h;
            return h;
        }

        private UndoState CaptureUndoState()
        {
            return new UndoState
            {
                Text = _editorText ?? "",
                Caret = _caretIndex,
                TopLine = _editorTopLine
            };
        }

        private void ApplyUndoState(UndoState s)
        {
            if (s == null) return;
            _editorText = s.Text ?? "";
            _caretIndex = Math.Max(0, Math.Min(_editorText.Length, s.Caret));
            _editorTopLine = Math.Max(0, s.TopLine);
            RebuildTextCache();
            _preferredColumn = -1;
            MarkDirty();
            EnsureCaretVisible();
        }

        private void PushUndoBeforeEdit()
        {
            string path = GetSelectedPathOrNull();
            if (string.IsNullOrEmpty(path))
                return;

            var h = GetUndoHistory(path, true);
            h.Undo.Push(CaptureUndoState());
            while (h.Undo.Count > MaxUndoStatesPerFile)
            {
                // Trim oldest by rebuilding stack order.
                var tmp = h.Undo.Reverse().Take(MaxUndoStatesPerFile).ToList();
                h.Undo.Clear();
                for (int i = tmp.Count - 1; i >= 0; i--) h.Undo.Push(tmp[i]);
            }
            h.Redo.Clear();
        }

        private void UndoEdit()
        {
            string path = GetSelectedPathOrNull();
            var h = GetUndoHistory(path, false);
            if (h == null || h.Undo.Count == 0)
            {
                SetStatus("Nothing to undo.", Color.Yellow);
                return;
            }

            h.Redo.Push(CaptureUndoState());
            var prev = h.Undo.Pop();
            ApplyUndoState(prev);
            SetStatus("Undo", Color.LightGreen);
        }

        private void RedoEdit()
        {
            string path = GetSelectedPathOrNull();
            var h = GetUndoHistory(path, false);
            if (h == null || h.Redo.Count == 0)
            {
                SetStatus("Nothing to redo.", Color.Yellow);
                return;
            }

            h.Undo.Push(CaptureUndoState());
            var next = h.Redo.Pop();
            ApplyUndoState(next);
            SetStatus("Redo", Color.LightGreen);
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

        private void RebuildSearchMatches()
        {
            _searchMatchIndices.Clear();
            _activeSearchMatch = -1;

            string term = (_editorSearchText ?? "").Trim();
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(_editorText))
                return;

            int start = 0;
            while (start < _editorText.Length)
            {
                int hit = _editorText.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                    break;

                _searchMatchIndices.Add(hit);
                start = hit + Math.Max(1, term.Length);
            }

            if (_searchMatchIndices.Count == 0)
            {
                SetStatus("No matches for \"" + term + "\"", Color.Yellow);
                return;
            }

            _activeSearchMatch = _searchMatchIndices.FindIndex(i => i >= _caretIndex);
            if (_activeSearchMatch < 0)
                _activeSearchMatch = 0;

            SetStatus("Found " + _searchMatchIndices.Count.ToString(CultureInfo.InvariantCulture) + " match(es) for \"" + term + "\"", Color.LightGreen);
        }

        private void JumpToNextSearchMatch()
        {
            if (_searchMatchIndices.Count == 0)
            {
                string term = (_editorSearchText ?? "").Trim();
                if (string.IsNullOrEmpty(term))
                    SetStatus("Type in Find-in-file first.", Color.Yellow);
                else
                    SetStatus("No matches for \"" + term + "\"", Color.Yellow);
                return;
            }

            if (_activeSearchMatch < 0 || _activeSearchMatch >= _searchMatchIndices.Count)
                _activeSearchMatch = 0;
            else
                _activeSearchMatch = (_activeSearchMatch + 1) % _searchMatchIndices.Count;

            _caretIndex = _searchMatchIndices[_activeSearchMatch];
            _preferredColumn = -1;
            EnsureCaretVisible();

            int shown = _activeSearchMatch + 1;
            int total = _searchMatchIndices.Count;
            SetStatus("Match " + shown.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture), Color.LightGreen);
        }

        private void DrawLineSearchHighlights(SpriteBatch sb, string lineText, int lineIndex, Vector2 textPos, int maxWidth)
        {
            string term = (_editorSearchText ?? "").Trim();
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(lineText))
                return;

            if (lineIndex < 0 || lineIndex >= _lineStarts.Count)
                return;

            int lineStart = _lineStarts[lineIndex];
            int searchInLine = 0;
            while (searchInLine < lineText.Length)
            {
                int localHit = lineText.IndexOf(term, searchInLine, StringComparison.OrdinalIgnoreCase);
                if (localHit < 0)
                    break;

                string prefix = lineText.Substring(0, localHit);
                string hitText = lineText.Substring(localHit, Math.Min(term.Length, lineText.Length - localHit));
                float x = textPos.X + _smallFont.MeasureString(prefix).X;
                float w = _smallFont.MeasureString(hitText).X;
                if (x < textPos.X + maxWidth)
                {
                    float right = Math.Min(textPos.X + maxWidth, x + w);
                    int drawW = Math.Max(1, (int)Math.Round(right - x));
                    int drawX = (int)Math.Round(x);
                    int drawY = (int)Math.Round(textPos.Y + 1);
                    int drawH = Math.Max(1, _smallFont.LineSpacing - 1);

                    bool isActive = _activeSearchMatch >= 0 &&
                                    _activeSearchMatch < _searchMatchIndices.Count &&
                                    _searchMatchIndices[_activeSearchMatch] == lineStart + localHit;

                    Color c = isActive
                        ? new Color(70, 190, 95, 210)
                        : new Color(45, 130, 65, 160);
                    sb.Draw(_white, new Rectangle(drawX, drawY, drawW, drawH), c);
                }

                searchInLine = localHit + Math.Max(1, term.Length);
            }
        }

        private struct ColorToken
        {
            public string Text;
            public Color Color;
        }

        private void DrawSyntaxLine(SpriteBatch sb, string lineText, Vector2 textPos, int maxWidth)
        {
            if (lineText == null)
                lineText = "";

            var tokens = TokenizeSyntaxLine(lineText);
            float x = textPos.X;
            float maxX = textPos.X + Math.Max(1, maxWidth);

            for (int i = 0; i < tokens.Count; i++)
            {
                string text = tokens[i].Text ?? "";
                if (text.Length == 0)
                    continue;

                float w = _smallFont.MeasureString(text).X;
                if (x + w <= maxX)
                {
                    var p = new Vector2(x, textPos.Y);
                    sb.DrawString(_smallFont, text, p + new Vector2(1, 1), Color.Black);
                    sb.DrawString(_smallFont, text, p, tokens[i].Color);
                    x += w;
                    continue;
                }

                // Last visible token: clip to available width.
                string clipped = ClipText(_smallFont, text, (int)Math.Max(1, maxX - x));
                if (!string.IsNullOrEmpty(clipped))
                {
                    var p = new Vector2(x, textPos.Y);
                    sb.DrawString(_smallFont, clipped, p + new Vector2(1, 1), Color.Black);
                    sb.DrawString(_smallFont, clipped, p, tokens[i].Color);
                }
                break;
            }
        }

        private void ValidateSelectedFile()
        {
            if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
            {
                SetStatus("Pick a config file first.", Color.Yellow);
                return;
            }

            string path = _files[_selectedFileIndex].FullPath;
            string ext = Path.GetExtension(path) ?? "";
            string text = _editorText ?? "";

            string msg;
            Color c;
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string jerr;
                    if (!ValidateJsonBasic(text, out jerr))
                    {
                        msg = jerr;
                        c = Color.OrangeRed;
                    }
                    else
                    {
                        msg = "Validation OK (JSON).";
                        c = Color.LightGreen;
                    }
                }
                catch (Exception ex)
                {
                    msg = "JSON error: " + ex.Message;
                    c = Color.OrangeRed;
                }
            }
            else if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var xd = new System.Xml.XmlDocument();
                    xd.LoadXml(text);
                    msg = "Validation OK (XML).";
                    c = Color.LightGreen;
                }
                catch (System.Xml.XmlException xex)
                {
                    msg = "XML error line " + xex.LineNumber.ToString(CultureInfo.InvariantCulture)
                        + ", pos " + xex.LinePosition.ToString(CultureInfo.InvariantCulture)
                        + ": " + xex.Message;
                    c = Color.OrangeRed;
                }
                catch (Exception ex)
                {
                    msg = "XML error: " + ex.Message;
                    c = Color.OrangeRed;
                }
            }
            else if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".conf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(ext))
            {
                int bad = 0;
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#")) continue;
                    if (t.StartsWith("[") && t.EndsWith("]")) continue;
                    if (t.Contains("=")) continue;
                    bad++;
                }
                if (bad == 0) { msg = "Validation OK (INI-style)."; c = Color.LightGreen; }
                else { msg = "INI-style warning: " + bad.ToString(CultureInfo.InvariantCulture) + " suspicious line(s)."; c = Color.Yellow; }
            }
            else
            {
                msg = "No validator for " + ext + " (edit/save still works).";
                c = new Color(185, 195, 210, 255);
            }

            _lastValidationMessage = msg;
            _lastValidationColor = c;
            SetStatus(msg, c);
        }

        private static bool ValidateJsonBasic(string text, out string error)
        {
            int line = 1;
            int col = 0;
            bool inString = false;
            bool escaped = false;
            var stack = new Stack<char>();

            for (int i = 0; i < (text ?? "").Length; i++)
            {
                char ch = text[i];
                if (ch == '\n')
                {
                    line++;
                    col = 0;
                }
                else
                {
                    col++;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == '"')
                        inString = false;

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{' || ch == '[')
                {
                    stack.Push(ch);
                    continue;
                }

                if (ch == '}' || ch == ']')
                {
                    if (stack.Count == 0)
                    {
                        error = "JSON error line " + line.ToString(CultureInfo.InvariantCulture)
                            + " pos " + col.ToString(CultureInfo.InvariantCulture)
                            + ": unexpected '" + ch + "'";
                        return false;
                    }

                    char open = stack.Pop();
                    if ((open == '{' && ch != '}') || (open == '[' && ch != ']'))
                    {
                        error = "JSON error line " + line.ToString(CultureInfo.InvariantCulture)
                            + " pos " + col.ToString(CultureInfo.InvariantCulture)
                            + ": mismatched '" + ch + "'";
                        return false;
                    }
                }
            }

            if (inString)
            {
                error = "JSON error: unterminated string literal.";
                return false;
            }

            if (stack.Count > 0)
            {
                error = "JSON error: unclosed object/array.";
                return false;
            }

            error = null;
            return true;
        }

        private void RestoreSelectedBak()
        {
            if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
            {
                SetStatus("Pick a config file first.", Color.Yellow);
                return;
            }

            try
            {
                string path = _files[_selectedFileIndex].FullPath;
                string bakPath = path + ".bak";
                if (!File.Exists(bakPath))
                {
                    SetStatus("No .bak found for selected file.", Color.Yellow);
                    return;
                }

                PushUndoBeforeEdit();
                File.Copy(bakPath, path, true);
                LoadSelectedFile();
                SetStatus("Restored .bak for " + _files[_selectedFileIndex].DisplayPath, Color.LightGreen);
            }
            catch (Exception ex)
            {
                SetStatus("Restore .bak failed: " + ex.Message, Color.OrangeRed);
            }
        }

        private void WriteBackupsBeforeSave(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }
            }
            catch { }

            try
            {
                if (!File.Exists(path))
                    return;

                string modsRoot = GetRuntimeModsRoot() ?? Path.GetDirectoryName(path);
                string backupRoot = Path.Combine(modsRoot, "Config", "!Backups");
                string rel = path;
                if (!string.IsNullOrEmpty(modsRoot) && path.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase))
                    rel = path.Substring(modsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string relDir = Path.GetDirectoryName(rel) ?? "";
                string fileName = Path.GetFileName(path);
                string safeDir = Path.Combine(backupRoot, relDir);
                Directory.CreateDirectory(safeDir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                string histPath = Path.Combine(safeDir, fileName + "." + stamp + ".bak");
                File.Copy(path, histPath, true);
            }
            catch { }
        }

        private void AddRecentPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return;

            _recentPaths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
            _recentPaths.Insert(0, fullPath);
            if (_recentPaths.Count > MaxRecentFiles)
                _recentPaths.RemoveRange(MaxRecentFiles, _recentPaths.Count - MaxRecentFiles);
        }

        private List<ColorToken> TokenizeSyntaxLine(string line)
        {
            var tokens = new List<ColorToken>(6);
            string trimmed = (line ?? "").TrimStart();

            Color def = new Color(230, 230, 235, 255);
            Color comment = new Color(120, 155, 120, 255);
            Color section = new Color(235, 208, 120, 255);
            Color key = new Color(120, 190, 255, 255);
            Color sep = new Color(180, 186, 196, 255);
            Color val = new Color(233, 170, 122, 255);
            Color num = new Color(190, 150, 255, 255);
            Color truthy = new Color(140, 220, 140, 255);
            Color falsy = new Color(235, 140, 140, 255);
            Color tag = new Color(145, 180, 255, 255);

            if (trimmed.StartsWith(";") || trimmed.StartsWith("#"))
            {
                tokens.Add(new ColorToken { Text = line, Color = comment });
                return tokens;
            }

            if (trimmed.StartsWith("[") && trimmed.Contains("]"))
            {
                tokens.Add(new ColorToken { Text = line, Color = section });
                return tokens;
            }

            // XML-ish
            if (trimmed.StartsWith("<") && trimmed.Contains(">"))
            {
                tokens.Add(new ColorToken { Text = line, Color = tag });
                return tokens;
            }

            // INI / key-value
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                string left = line.Substring(0, eq);
                string right = eq + 1 < line.Length ? line.Substring(eq + 1) : "";
                tokens.Add(new ColorToken { Text = left, Color = key });
                tokens.Add(new ColorToken { Text = "=", Color = sep });
                tokens.Add(new ColorToken { Text = right, Color = PickValueColor(right, val, num, truthy, falsy) });
                return tokens;
            }

            // JSON-ish key:value
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string left = line.Substring(0, colon);
                string right = colon + 1 < line.Length ? line.Substring(colon + 1) : "";
                tokens.Add(new ColorToken { Text = left, Color = key });
                tokens.Add(new ColorToken { Text = ":", Color = sep });
                tokens.Add(new ColorToken { Text = right, Color = PickValueColor(right, val, num, truthy, falsy) });
                return tokens;
            }

            tokens.Add(new ColorToken { Text = line, Color = def });
            return tokens;
        }

        private static Color PickValueColor(string valueText, Color val, Color num, Color truthy, Color falsy)
        {
            string t = (valueText ?? "").Trim();
            if (t.Length == 0)
                return val;

            if ((t.StartsWith("\"") && t.EndsWith("\"")) || (t.StartsWith("'") && t.EndsWith("'")))
                return val;

            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "yes", StringComparison.OrdinalIgnoreCase))
                return truthy;

            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "no", StringComparison.OrdinalIgnoreCase))
                return falsy;

            double n;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                return num;

            return val;
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
            int maxScroll = Math.Max(0, _visibleRows.Count - visibleRows);

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
            int availableHeight = (_filesRect.Bottom - 6) - GetFilesContentTop();
            return Math.Max(1, availableHeight / GetFileRowStep());
        }

        private int GetFilesContentTop()
        {
            if (HideLeftHeaderAndFilter)
                return _filesRect.Top + 6;

            return _filesRect.Top + (GetFileRowHeight() + FileHeaderExtraHeight) + 6 + InputBoxHeight + FileContentTopGap;
        }

        private void UpdateFileScrollbarRects()
        {
            int rowHeight = GetFileRowHeight();
            int contentTop = GetFilesContentTop();
            int contentBottom = _filesRect.Bottom - 6;
            int trackHeight = Math.Max(8, contentBottom - contentTop);

            _fileScrollbarTrackRect = new Rectangle(
                _filesRect.Right - FileScrollbarMargin - FileScrollbarWidth,
                contentTop,
                FileScrollbarWidth,
                trackHeight);

            int visibleRows = GetVisibleFileRowCount();
            int totalRows = Math.Max(visibleRows, _visibleRows.Count);
            _fileScrollbarVisible = _visibleRows.Count > visibleRows;

            if (!_fileScrollbarVisible)
            {
                _fileScrollbarThumbRect = new Rectangle(_fileScrollbarTrackRect.X, _fileScrollbarTrackRect.Y, _fileScrollbarTrackRect.Width, _fileScrollbarTrackRect.Height);
                return;
            }

            float visibleRatio = (float)visibleRows / totalRows;
            int thumbHeight = Math.Max(FileScrollbarMinThumbHeight, (int)Math.Round(_fileScrollbarTrackRect.Height * visibleRatio));
            thumbHeight = Math.Min(thumbHeight, _fileScrollbarTrackRect.Height);

            int maxScroll = Math.Max(1, _visibleRows.Count - visibleRows);
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
            int maxScroll = Math.Max(0, _visibleRows.Count - visibleRows);
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
            int maxScroll = Math.Max(0, _visibleRows.Count - visibleRows);

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

        private void EnsureDebugPaths() { }

        private void DebugTrace(string message)
        {
            // Debug file/log tracing removed in v2.
        }

        private void RebuildVisibleFileIndices(bool keepSelectionVisible)
        {
            _visibleFileIndices.Clear();
            _visibleRows.Clear();
            string filter = (_fileFilterText ?? "").Trim();

            if (_showRecentTab)
            {
                for (int r = 0; r < _recentPaths.Count; r++)
                {
                    string rp = _recentPaths[r];
                    int fi = _files.FindIndex(f => string.Equals(f.FullPath, rp, StringComparison.OrdinalIgnoreCase));
                    if (fi < 0)
                        continue;
                    if (!string.IsNullOrEmpty(filter) &&
                        _files[fi].DisplayPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    _visibleFileIndices.Add(fi);
                    _visibleRows.Add(new FileListRow
                    {
                        IsGroup = false,
                        FileIndex = fi,
                        GroupKey = GetGroupKey(_files[fi]),
                        GroupLabel = GetGroupLabel(_files[fi])
                    });
                }
            }
            else
            {
                bool hasFilter = !string.IsNullOrEmpty(filter);
                for (int g = 0; g < _groupBuckets.Count; g++)
                {
                    var bucket = _groupBuckets[g];
                    if (bucket == null)
                        continue;

                    bool groupMatch = hasFilter && bucket.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    var matched = new List<int>();
                    for (int i = 0; i < bucket.FileIndices.Count; i++)
                    {
                        int fi = bucket.FileIndices[i];
                        if (fi < 0 || fi >= _files.Count)
                            continue;
                        if (!hasFilter || groupMatch || _files[fi].DisplayPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            matched.Add(fi);
                    }

                    if (matched.Count == 0)
                        continue;

                    _visibleRows.Add(new FileListRow
                    {
                        IsGroup = true,
                        GroupKey = bucket.Key,
                        GroupLabel = bucket.Label,
                        FileIndex = -1
                    });

                    bool expanded = _expandedGroups.Contains(bucket.Key) || hasFilter;
                    if (!expanded)
                        continue;

                    for (int m = 0; m < matched.Count; m++)
                    {
                        int fi = matched[m];
                        _visibleFileIndices.Add(fi);
                        _visibleRows.Add(new FileListRow
                        {
                            IsGroup = false,
                            GroupKey = bucket.Key,
                            GroupLabel = bucket.Label,
                            FileIndex = fi
                        });
                    }
                }
            }

            if (_visibleRows.Count == 0)
            {
                _selectedFileIndex = -1;
                _fileScroll = 0;
                DebugTrace($"RebuildVisibleFileIndices -> 0 results (filter=\"{filter}\")");
                return;
            }

            if (_selectedFileIndex < 0 || !_visibleFileIndices.Contains(_selectedFileIndex))
            {
                _selectedFileIndex = _visibleFileIndices.Count > 0 ? _visibleFileIndices[0] : -1;
                if (keepSelectionVisible)
                    LoadSelectedFile();
            }

            EnsureSelectedFileVisible();
            DebugTrace($"RebuildVisibleFileIndices -> rows={_visibleRows.Count}, visibleFiles={_visibleFileIndices.Count}, selected={_selectedFileIndex}, filter=\"{filter}\"");
        }

        private void BuildGroupBuckets()
        {
            _groupBuckets.Clear();
            _expandedGroups.Clear();

            var byKey = new Dictionary<string, GroupBucket>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _files.Count; i++)
            {
                var item = _files[i];
                string key = GetGroupKey(item);
                GroupBucket bucket;
                if (!byKey.TryGetValue(key, out bucket))
                {
                    bucket = new GroupBucket { Key = key, Label = GetGroupLabel(item) };
                    byKey[key] = bucket;
                    _groupBuckets.Add(bucket);
                }
                bucket.FileIndices.Add(i);
            }
        }

        private void ToggleGroupExpanded(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey))
                return;

            if (_expandedGroups.Contains(groupKey))
                _expandedGroups.Remove(groupKey);
            else
                _expandedGroups.Add(groupKey);

            RebuildVisibleFileIndices(true);
        }

        private static string GetGroupKey(ConfigFileItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DisplayPath))
                return "_root";

            var parts = item.DisplayPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "_root";

            return parts[0];
        }

        private static string GetGroupLabel(ConfigFileItem item)
        {
            string key = GetGroupKey(item);
            return string.Equals(key, "_root", StringComparison.OrdinalIgnoreCase) ? "(Root)" : key;
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
                if (_selectedFileIndex < 0 || _selectedFileIndex >= _files.Count)
                    return;

                string selectedPath = _files[_selectedFileIndex].FullPath;
                if (!File.Exists(selectedPath))
                {
                    BeginRefreshFileList();
                    SetStatus("Hot-reload: selected file was removed. Refreshing list...", Color.Yellow);
                    return;
                }

                var writeUtc = File.GetLastWriteTimeUtc(selectedPath);
                if (writeUtc != _selectedFileLastWriteUtc)
                {
                    LoadSelectedFile();
                    SetStatus("Hot-reload: selected file changed on disk and was reloaded.", Color.LightGreen);
                }
            }
            catch (Exception ex)
            {
                DebugTrace("PollHotReload exception: " + ex.Message);
            }
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
            int visitedDirs = 0;

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                visitedDirs++;
                if (visitedDirs > MaxEnumeratedDirectories)
                    yield break;

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

                        try
                        {
                            var attrs = File.GetAttributes(childDirs[d]);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                                continue;
                        }
                        catch
                        {
                            continue;
                        }

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

