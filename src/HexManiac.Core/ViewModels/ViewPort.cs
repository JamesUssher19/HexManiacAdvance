﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.Models.Runs.Factory;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using HavenSoft.HexManiac.Core.ViewModels.Visitors;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using static HavenSoft.HexManiac.Core.ICommandExtensions;
using static HavenSoft.HexManiac.Core.Models.Runs.ArrayRun;
using static HavenSoft.HexManiac.Core.Models.Runs.BaseRun;
using static HavenSoft.HexManiac.Core.Models.Runs.PCSRun;
using static HavenSoft.HexManiac.Core.Models.Runs.PointerRun;
using HavenSoft.HexManiac.Core.ViewModels.QuickEditItems;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// A range of visible data that should be displayed.
   /// </summary>
   public class ViewPort : ViewModelCore, IViewPort {
      public const string AllHexCharacters = "0123456789ABCDEFabcdef";
      public const char GotoMarker = '@';
      public const char CommentStart = '#';
      public const int CopyLimit = 20000;

      private static readonly NotifyCollectionChangedEventArgs ResetArgs = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
      private readonly StubCommand
         undoWrapper = new StubCommand(),
         redoWrapper = new StubCommand(),
         clear = new StubCommand(),
         selectAll = new StubCommand(),
         copy = new StubCommand(),
         copyAddress = new StubCommand(),
         copyBytes = new StubCommand(),
         deepCopy = new StubCommand(),
         isText = new StubCommand();

      public Singletons Singletons { get; }

      private HexElement[,] currentView;
      private bool exitEditEarly, withinComment;

      public string Name {
         get {
            var name = Path.GetFileNameWithoutExtension(FileName);
            if (string.IsNullOrEmpty(name)) name = "Untitled";
            if (history.HasDataChange) name += "*";
            return name;
         }
      }

      private string fileName;
      public string FileName {
         get => fileName;
         private set {
            if (TryUpdate(ref fileName, value) && !string.IsNullOrEmpty(fileName)) {
               FullFileName = Path.GetFullPath(fileName);
               NotifyPropertyChanged(nameof(Name));
            }
         }
      }

      private string fullFileName;
      public string FullFileName { get => fullFileName; private set => TryUpdate(ref fullFileName, value); }

      #region Scrolling Properties

      private readonly ScrollRegion scroll;

      public event EventHandler PreviewScrollChanged;

      public int Width {
         get => scroll.Width;
         set {
            using (ModelCacheScope.CreateScope(Model)) selection.ChangeWidth(value);
         }
      }

      public int Height {
         get => scroll.Height;
         set {
            using (ModelCacheScope.CreateScope(Model)) scroll.Height = value;
         }
      }

      public int MinimumScroll => scroll.MinimumScroll;

      public int ScrollValue {
         get => scroll.ScrollValue;
         set {
            PreviewScrollChanged?.Invoke(this, EventArgs.Empty);
            using (ModelCacheScope.CreateScope(Model)) scroll.ScrollValue = value;
         }
      }

      public int MaximumScroll => scroll.MaximumScroll;

      public ObservableCollection<string> Headers => scroll.Headers;
      public ObservableCollection<HeaderRow> ColumnHeaders { get; }
      public int DataOffset => scroll.DataIndex;
      public ICommand Scroll => scroll.Scroll;

      public bool UseCustomHeaders {
         get => scroll.UseCustomHeaders;
         set { using (ModelCacheScope.CreateScope(Model)) scroll.UseCustomHeaders = value; }
      }

      private void ScrollPropertyChanged(object sender, PropertyChangedEventArgs e) {
         if (e.PropertyName == nameof(scroll.DataIndex)) {
            RefreshBackingData();
            if (e is ExtendedPropertyChangedEventArgs<int> ex) {
               var previous = ex.OldValue;
               if (Math.Abs(scroll.DataIndex - previous) % Width != 0) UpdateColumnHeaders();
            }
         } else if (e.PropertyName != nameof(scroll.DataLength)) {
            NotifyPropertyChanged(e);
         }

         if (e.PropertyName == nameof(Width) || e.PropertyName == nameof(Height)) {
            RefreshBackingData();
         }

         if (e.PropertyName == nameof(Width)) {
            UpdateColumnHeaders();
            NotifyPropertyChanged(nameof(ScrollValue)); // changing the Scroll's Width can mess with the ScrollValue: go ahead and notify
         }
      }

      #endregion

      #region Selection Properties

      private readonly Selection selection;

      private bool stretchData;
      public bool ShowWidthProperties => !AnchorTextVisible;
      public bool StretchData { get => stretchData; set => Set(ref stretchData, value); }
      public bool AutoAdjustDataWidth { get => selection.AutoAdjustDataWidth; set => selection.AutoAdjustDataWidth = value; }
      public bool AllowMultipleElementsPerLine { get => selection.AllowMultipleElementsPerLine; set => selection.AllowMultipleElementsPerLine = value; }

      public Point SelectionStart {
         get => selection.SelectionStart;
         set => selection.SelectionStart = value;
      }

      public Point SelectionEnd {
         get => selection.SelectionEnd;
         set => selection.SelectionEnd = value;
      }

      public int PreferredWidth {
         get => selection.PreferredWidth;
         set => selection.PreferredWidth = value;
      }
      private readonly StubCommand moveSelectionStart = new StubCommand(),
         moveSelectionEnd = new StubCommand();
      public ICommand MoveSelectionStart => moveSelectionStart;
      public ICommand MoveSelectionEnd => moveSelectionEnd;
      public ICommand Goto => selection.Goto;
      public ICommand Back => selection.Back;
      public ICommand Forward => selection.Forward;
      public ICommand ResetAlignment => selection.ResetAlignment;
      public ICommand SelectAll => selectAll;

      private void ClearActiveEditBeforeSelectionChanges(object sender, Point location) {
         if (location.X >= 0 && location.X < scroll.Width && location.Y >= 0 && location.Y < scroll.Height) {
            var element = this[location.X, location.Y];
            var underEdit = element.Format as UnderEdit;
            if (underEdit != null) {
               using (ModelCacheScope.CreateScope(Model)) {
                  if (underEdit.CurrentText == string.Empty) {
                     var index = scroll.ViewPointToDataIndex(location);
                     var operation = new DataClear(Model, history.CurrentChange, index);
                     underEdit.OriginalFormat.Visit(operation, Model[index]);
                     ClearEdits(location);
                  } else {
                     var endEdit = " ";
                     if (underEdit.CurrentText.Count(c => c == StringDelimeter) % 2 == 1) endEdit = StringDelimeter.ToString();
                     var originalFormat = underEdit.OriginalFormat;
                     if (originalFormat is Anchor anchor) originalFormat = anchor.OriginalFormat;
                     if (underEdit.CurrentText.StartsWith(EggMoveRun.GroupStart) && (originalFormat is EggSection || originalFormat is EggItem)) endEdit = EggMoveRun.GroupEnd;
                     currentView[location.X, location.Y] = new HexElement(element.Value, element.Edited, underEdit.Edit(endEdit));
                     if (!TryCompleteEdit(location)) ClearEdits(location);
                  }
               }
            }
         }
      }

      private void SelectionPropertyChanged(object sender, PropertyChangedEventArgs e) {
         if (e.PropertyName == nameof(SelectionEnd)) history.ChangeCompleted();
         NotifyPropertyChanged(e.PropertyName);
         var dataIndex = scroll.ViewPointToDataIndex(SelectionStart);
         using (ModelCacheScope.CreateScope(Model)) {
            UpdateToolsFromSelection(dataIndex);
            UpdateSelectedAddress();
            UpdateSelectedBytes();
         }
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
      }

      private void UpdateToolsFromSelection(int dataIndex) {
         var run = Model.GetNextRun(dataIndex);
         if (run.Start > dataIndex) {
            AnchorTextVisible = false;
            return;
         }

         // if the user explicitly closed the tools, don't auto-open them.
         if (tools.SelectedIndex != -1) {
            // if the 'Raw' tool is selected, don't auto-update tool selection.
            if (!(tools.SelectedTool == tools.CodeTool && tools.CodeTool.Mode == CodeMode.Raw)) {
               using (ModelCacheScope.CreateScope(Model)) {
                  // update the tool from pointers too
                  if (run is PointerRun) {
                     run = Model.GetNextRun(Model.ReadPointer(run.Start));
                     dataIndex = run.Start;
                  }
                  if (run is ISpriteRun) {
                     tools.SpriteTool.SpriteAddress = run.Start;
                     tools.SelectedIndex = tools.IndexOf(tools.SpriteTool);
                  } else if (run is IPaletteRun) {
                     tools.SpriteTool.PaletteAddress = run.Start;
                     tools.SelectedIndex = tools.IndexOf(tools.SpriteTool);
                  } else if (run is ITableRun array) {
                     var offsets = array.ConvertByteOffsetToArrayOffset(dataIndex);
                     Tools.StringTool.Address = offsets.SegmentStart - offsets.ElementIndex * array.ElementLength;
                     Tools.TableTool.Address = array.Start + array.ElementLength * offsets.ElementIndex;
                     if (!(run is IStreamRun || array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.PCS) || tools.SelectedTool != tools.StringTool) {
                        tools.SelectedIndex = tools.IndexOf(tools.TableTool);
                     }
                  } else if (run is IStreamRun) {
                     Tools.StringTool.Address = run.Start;
                     tools.SelectedIndex = tools.IndexOf(tools.StringTool);
                  } else {
                     // not a special run, so don't update tools
                  }
               }
            }
         }

         if (this[SelectionStart].Format is Anchor anchor) {
            TryUpdate(ref anchorText, AnchorStart + anchor.Name + anchor.Format, nameof(AnchorText));
            AnchorTextVisible = true;
         } else {
            AnchorTextVisible = false;
         }
      }

      private string selectedAddress;
      public string SelectedAddress {
         get => selectedAddress;
         private set => TryUpdate(ref selectedAddress, value);
      }

      private void UpdateSelectedAddress() {
         var dataIndex1 = scroll.ViewPointToDataIndex(SelectionStart);
         var dataIndex2 = scroll.ViewPointToDataIndex(SelectionEnd);
         var left = Math.Min(dataIndex1, dataIndex2);
         var result = "Address: " + left.ToString("X6");

         var run = Model.GetNextRun(left);
         if (run is ITableRun array1 && array1.Start <= left) {
            var index = array1.ConvertByteOffsetToArrayOffset(left).ElementIndex;
            var basename = Model.GetAnchorFromAddress(-1, array1.Start);
            if (array1.ElementNames.Count > index) {
               result += $" | {basename}/{array1.ElementNames[index]}";
            } else {
               result += $" | {basename}/{index}";
            }
         } else if (run.PointerSources != null && run.PointerSources.Count > 0 && string.IsNullOrEmpty(Model.GetAnchorFromAddress(-1, run.Start))) {
            var sourceRun = Model.GetNextRun(run.PointerSources[0]);
            if (sourceRun is ITableRun array2) {
               // we are an anchor that's pointed to from an array
               var offset = array2.ConvertByteOffsetToArrayOffset(run.PointerSources[0]);
               var index = offset.ElementIndex;
               if (index >= 0) {
                  var segment = array2.ElementContent[offset.SegmentIndex];
                  var basename = Model.GetAnchorFromAddress(-1, array2.Start);
                  if (array2.ElementNames.Count > index) {
                     result += $" | {basename}/{array2.ElementNames[index]}/{segment.Name}";
                  } else {
                     result += $" | {basename}/{index}/{segment.Name}";
                  }
               }
            }
         }

         if (!SelectionStart.Equals(SelectionEnd)) {
            int length = Math.Abs(dataIndex1 - dataIndex2) + 1;
            result += $" | {length} bytes selected";
         }

         SelectedAddress = result;
      }

      private string selectedBytes;
      public string SelectedBytes {
         get {
            if (selectedBytes != null) return selectedBytes;

            var bytes = GetSelectedByteContents(0x10);
            selectedBytes = "Selected Bytes: " + bytes;
            return selectedBytes;
         }
         private set => TryUpdate(ref selectedBytes, value);
      }

      // update the selected bytes lazily. Most of the time we don't really care about the new value.
      private void UpdateSelectedBytes() => SelectedBytes = null;

      private string GetSelectedByteContents(int maxByteCount = int.MaxValue) {
         var dataIndex1 = scroll.ViewPointToDataIndex(SelectionStart);
         var dataIndex2 = scroll.ViewPointToDataIndex(SelectionEnd);
         var left = Math.Min(dataIndex1, dataIndex2);
         var length = Math.Abs(dataIndex1 - dataIndex2) + 1;
         if (left < 0) { length += left; left = 0; }
         if (left + length > Model.Count) length = Model.Count - left;
         var result = new StringBuilder();
         for (int i = 0; i < length && i < maxByteCount; i++) {
            var token = Model[left + i].ToHexString();
            result.Append(token);
            result.Append(" ");
         }
         if (maxByteCount < length) result.Append("...");
         return result.ToString();
      }

      private void SelectAllExecuted() {
         Goto.Execute(0);
         SelectionStart = new Point(0, 0);
         SelectionEnd = scroll.DataIndexToViewPoint(Model.Count - 1);
      }

      #endregion

      #region Undo / Redo

      private readonly ChangeHistory<ModelDelta> history;

      public ChangeHistory<ModelDelta> ChangeHistory => history;

      public ModelDelta CurrentChange => history.CurrentChange;

      public ICommand Undo => undoWrapper;

      public ICommand Redo => redoWrapper;

      private ModelDelta RevertChanges(ModelDelta changes) {
         var reverse = changes.Revert(Model);
         RefreshBackingData();
         return reverse;
      }

      private void HistoryPropertyChanged(object sender, PropertyChangedEventArgs e) {
         if (e.PropertyName == nameof(history.IsSaved)) {
            save.CanExecuteChanged.Invoke(save, EventArgs.Empty);
            if (history.IsSaved) { Model.ResetChanges(); RefreshBackingData(); }
         }

         if (e.PropertyName == nameof(history.HasDataChange)) NotifyPropertyChanged(nameof(Name));
      }

      #endregion

      #region Saving

      private readonly StubCommand
         save = new StubCommand(),
         saveAs = new StubCommand(),
         close = new StubCommand();

      public ICommand Save => save;

      public ICommand SaveAs => saveAs;

      public ICommand Close => close;

      public event EventHandler Closed;

      private void SaveExecuted(IFileSystem fileSystem) {
         if (history.IsSaved) return;

         if (string.IsNullOrEmpty(FileName)) {
            SaveAsExecuted(fileSystem);
            return;
         }

         var metadata = Model.ExportMetadata(Singletons.MetadataInfo);
         if (fileSystem.Save(new LoadedFile(FileName, Model.RawData))) {
            fileSystem.SaveMetadata(FileName, metadata?.Serialize());
            history.TagAsSaved();
            Model.ResetChanges();
         }
      }

      private void SaveAsExecuted(IFileSystem fileSystem) {
         var newName = fileSystem.RequestNewName(FileName, "GameBoy Advanced", "gba");
         if (newName == null) return;

         var metadata = Model.ExportMetadata(Singletons.MetadataInfo);
         if (fileSystem.Save(new LoadedFile(newName, Model.RawData))) {
            FileName = newName; // don't bother notifying, because tagging the history will cause a notify;
            fileSystem.SaveMetadata(FileName, metadata?.Serialize());
            history.TagAsSaved();
            Model.ResetChanges();
         }
      }

      private void CloseExecuted(IFileSystem fileSystem) {
         if (!history.IsSaved) {
            var metadata = Model.ExportMetadata(Singletons.MetadataInfo);
            var result = fileSystem.TrySavePrompt(new LoadedFile(FileName, Model.RawData));
            if (result == null) return;
            if (result == true) {
               fileSystem.SaveMetadata(FileName, metadata?.Serialize());
            }
         }
         Closed?.Invoke(this, EventArgs.Empty);
      }

      #endregion

      #region Progress

      private double progress;
      public double Progress { get => progress; set => Set(ref progress, value); }

      private bool updateInProgress;
      public bool UpdateInProgress { get => updateInProgress; set => Set(ref updateInProgress, value); }

      private int initialWorkLoad;
      private List<IDisposable> CurrentProgressScopes = new List<IDisposable>();

      #endregion

      public int FreeSpaceStart { get => Model.FreeSpaceStart; set {
            if (Model.FreeSpaceStart != value) {
               Model.FreeSpaceStart = value;
               NotifyPropertyChanged();
            }
         }
      }

      private readonly ToolTray tools;
      public bool HasTools => true;
      public IToolTrayViewModel Tools => tools;

      private bool anchorTextVisible;
      public bool AnchorTextVisible {
         get => anchorTextVisible;
         set => Set(ref anchorTextVisible, value, arg => NotifyPropertyChanged(nameof(ShowWidthProperties)));
      }

      private string anchorText;
      public string AnchorText {
         get => anchorText;
         set {
            if (value == null) value = string.Empty;
            if (!value.StartsWith(AnchorStart.ToString())) value = AnchorStart + value;
            if (TryUpdate(ref anchorText, value)) {
               using (ModelCacheScope.CreateScope(Model)) {
                  var index = scroll.ViewPointToDataIndex(SelectionStart);
                  var run = Model.GetNextRun(index);
                  if (run.Start == index) {
                     var errorInfo = PokemonModel.ApplyAnchor(Model, history.CurrentChange, index, AnchorText);
                     if (errorInfo == ErrorInfo.NoError) {
                        OnError?.Invoke(this, string.Empty);
                        var newRun = Model.GetNextRun(index);
                        if (AnchorText == AnchorStart.ToString()) Model.ClearFormat(history.CurrentChange, run.Start, 1);
                        if (newRun is ArrayRun array) {
                           // if the format changed (ignoring length), run a goto to update the display width
                           if (run is ArrayRun array2 && !array.HasSameSegments(array2)) {
                              selection.PropertyChanged -= SelectionPropertyChanged; // to keep from double-updating the AnchorText
                              Goto.Execute(index.ToString("X2"));
                              selection.PropertyChanged += SelectionPropertyChanged;
                           }
                           UpdateColumnHeaders();
                        }
                        Tools.RefreshContent();
                        RefreshBackingData();
                     } else {
                        OnError?.Invoke(this, errorInfo.ErrorMessage);
                     }
                  }
               }
            }
         }
      }

      public ICommand Copy => copy;
      public ICommand CopyAddress => copyAddress;
      public ICommand CopyBytes => copyBytes;
      public ICommand DeepCopy => deepCopy;
      public ICommand Clear => clear;
      public ICommand IsText => isText;

      public HexElement this[Point p] => this[p.X, p.Y];

      public HexElement this[int x, int y] {
         get {
            if (x < 0 || x >= Width || x >= currentView.GetLength(0)) return HexElement.Undefined;
            if (y < 0 || y >= Height || y >= currentView.GetLength(1)) return HexElement.Undefined;
            if (currentView[x, y] is object) return currentView[x, y];

            if (x == 0 && y == 0) {
               RefreshBackingDataFull();
               return currentView[x, y];
            }

            using (ModelCacheScope.CreateScope(Model)) {
               RefreshBackingData(new Point(x, y));
            }

            return currentView[x, y];
         }
      }

      public IDataModel Model { get; private set; }

      public bool FormattedDataIsSelected {
         get {
            var (left, right) = (scroll.ViewPointToDataIndex(SelectionStart), scroll.ViewPointToDataIndex(SelectionEnd));
            if (left > right) (left, right) = (right, left);
            var nextRun = Model.GetNextRun(left);
            return nextRun.Start <= right;
         }
      }

#pragma warning disable 0067 // it's ok if events are never used
      public event EventHandler<string> OnError;
      public event EventHandler<string> OnMessage;
      public event EventHandler ClearMessage;
      public event NotifyCollectionChangedEventHandler CollectionChanged;
      public event EventHandler<ITabContent> RequestTabChange;
      public event EventHandler<Action> RequestDelayedWork;
      public event EventHandler RequestMenuClose;
#pragma warning restore 0067

      #region Constructors

      public ViewPort() : this(new LoadedFile(string.Empty, new byte[0])) { }

      public ViewPort(string fileName, IDataModel model, Singletons singletons = null, ChangeHistory<ModelDelta> changeHistory = null) {
         Singletons = singletons ?? new Singletons();
         history = changeHistory ?? new ChangeHistory<ModelDelta>(RevertChanges);
         history.PropertyChanged += HistoryPropertyChanged;

         Model = model;
         FileName = fileName;
         ColumnHeaders = new ObservableCollection<HeaderRow>();

         scroll = new ScrollRegion(model.TryGetUsefulHeader) { DataLength = Model.Count };
         scroll.PropertyChanged += ScrollPropertyChanged;

         selection = new Selection(scroll, Model, GetSelectionSpan);
         selection.PropertyChanged += SelectionPropertyChanged;
         selection.PreviewSelectionStartChanged += ClearActiveEditBeforeSelectionChanges;
         selection.OnError += (sender, e) => OnError?.Invoke(this, e);

         tools = new ToolTray(Singletons, Model, selection, history, this);
         Tools.OnError += (sender, e) => OnError?.Invoke(this, e);
         Tools.OnMessage += (sender, e) => RaiseMessage(e);
         tools.RequestMenuClose += (sender, e) => RequestMenuClose?.Invoke(this, e);
         Tools.StringTool.ModelDataChanged += ModelChangedByTool;
         Tools.StringTool.ModelDataMoved += ModelDataMovedByTool;
         Tools.TableTool.ModelDataChanged += ModelChangedByTool;
         Tools.TableTool.ModelDataMoved += ModelDataMovedByTool;
         Tools.CodeTool.ModelDataChanged += ModelChangedByCodeTool;
         Tools.CodeTool.ModelDataMoved += ModelDataMovedByTool;

         scroll.Scheduler = tools;
         ImplementCommands();
         if (changeHistory == null) CascadeScripts(); // if we're sharing history with another viewmodel, our model has already been updated like this.
         RefreshBackingData();
      }

      public ViewPort(LoadedFile file) : this(file.Name, new BasicModel(file.Contents)) { }

      private void ImplementCommands() {
         undoWrapper.CanExecute = history.Undo.CanExecute;
         undoWrapper.Execute = arg => { history.Undo.Execute(arg); using (ModelCacheScope.CreateScope(Model)) tools.RefreshContent(); };
         history.Undo.CanExecuteChanged += (sender, e) => undoWrapper.CanExecuteChanged.Invoke(undoWrapper, e);

         redoWrapper.CanExecute = history.Redo.CanExecute;
         redoWrapper.Execute = arg => { history.Redo.Execute(arg); using (ModelCacheScope.CreateScope(Model)) tools.RefreshContent(); };
         history.Redo.CanExecuteChanged += (sender, e) => redoWrapper.CanExecuteChanged.Invoke(redoWrapper, e);

         clear.CanExecute = CanAlwaysExecute;
         clear.Execute = arg => {
            var selectionStart = scroll.ViewPointToDataIndex(selection.SelectionStart);
            var selectionEnd = scroll.ViewPointToDataIndex(selection.SelectionEnd);
            var left = Math.Min(selectionStart, selectionEnd);
            var right = Math.Max(selectionStart, selectionEnd);
            var startRun = Model.GetNextRun(left);
            var endRun = Model.GetNextRun(right);
            if (startRun == endRun && startRun.Start <= left && (startRun.Start < left || startRun.Start + startRun.Length - 1 > right) && startRun is ITableRun arrayRun) {
               for (int i = 0; i < arrayRun.ElementCount; i++) {
                  var start = arrayRun.Start + arrayRun.ElementLength * i;
                  if (start + arrayRun.ElementLength <= left) continue;
                  if (start > right) break;
                  for (int j = 0; j < arrayRun.ElementLength; j++) history.CurrentChange.ChangeData(Model, start + j, 0xFF);
               }
            } else {
               Model.ClearFormatAndData(history.CurrentChange, left, right - left + 1);
            }
            RefreshBackingData();
         };

         copy.CanExecute = CanAlwaysExecute;
         copy.Execute = arg => {
            var selectionStart = scroll.ViewPointToDataIndex(selection.SelectionStart);
            var selectionEnd = scroll.ViewPointToDataIndex(selection.SelectionEnd);
            var left = Math.Min(selectionStart, selectionEnd);
            var length = Math.Abs(selectionEnd - selectionStart) + 1;
            if (length > CopyLimit) {
               OnError?.Invoke(this, $"Cannot copy more than {CopyLimit} bytes at once!");
            } else {
               bool usedHistory = false;
               ((IFileSystem)arg).CopyText = Model.Copy(() => { usedHistory = true; return history.CurrentChange; }, left, length);
               RefreshBackingData();
               if (usedHistory) UpdateToolsFromSelection(left);
            }
            RequestMenuClose?.Invoke(this, EventArgs.Empty);
         };

         copyAddress.CanExecute = CanAlwaysExecute;
         copyAddress.Execute = arg => {
            var fileSystem = (IFileSystem)arg;
            CopyAddressExecute(fileSystem);
         };

         copyBytes.CanExecute = CanAlwaysExecute;
         copyBytes.Execute = arg => {
            var fileSystem = (IFileSystem)arg;
            CopyBytesExecute(fileSystem);
         };

         deepCopy.CanExecute = CanAlwaysExecute;
         deepCopy.Execute = arg => {
            var fileSystem = (IFileSystem)arg;
            DeepCopyExecute(fileSystem);
         };

         moveSelectionStart.CanExecute = selection.MoveSelectionStart.CanExecute;
         moveSelectionStart.Execute = arg => {
            var direction = (Direction)arg;
            using (ModelCacheScope.CreateScope(Model)) {
               MoveSelectionStartExecuted(arg, direction);
            }
         };
         selection.MoveSelectionStart.CanExecuteChanged += (sender, e) => moveSelectionStart.CanExecuteChanged.Invoke(this, e);
         moveSelectionEnd.CanExecute = selection.MoveSelectionEnd.CanExecute;
         moveSelectionEnd.Execute = arg => {
            using (ModelCacheScope.CreateScope(Model)) {
               selection.MoveSelectionEnd.Execute(arg);
            }
         };
         selection.MoveSelectionEnd.CanExecuteChanged += (sender, e) => moveSelectionEnd.CanExecuteChanged.Invoke(this, e);

         isText.CanExecute = CanAlwaysExecute;
         isText.Execute = IsTextExecuted;

         save.CanExecute = arg => !history.IsSaved;
         save.Execute = arg => SaveExecuted((IFileSystem)arg);

         saveAs.CanExecute = CanAlwaysExecute;
         saveAs.Execute = arg => SaveAsExecuted((IFileSystem)arg);

         close.CanExecute = CanAlwaysExecute;
         close.Execute = arg => CloseExecuted((IFileSystem)arg);

         selectAll.CanExecute = CanAlwaysExecute;
         selectAll.Execute = arg => SelectAllExecuted();
      }

      /// <summary>
      /// Top-level scripts may be available through metadata.
      /// Find scripts called by those scripts, and add runs for those too.
      /// </summary>
      private void CascadeScripts() {
         var noChange = new NoDataChangeDeltaModel();
         using (ModelCacheScope.CreateScope(Model)) {
            foreach (var run in Runs(Model).OfType<IScriptStartRun>().ToList()) {
               if (run is XSERun) {
                  tools.CodeTool.ScriptParser.FormatScript<XSERun>(noChange, Model, run.Start);
               } else if (run is BSERun) {
                  tools.CodeTool.BattleScriptParser.FormatScript<BSERun>(noChange, Model, run.Start);
               }
            }
         }
      }

      private static IEnumerable<IFormattedRun> Runs(IDataModel model) {
         for (var run = model.GetNextRun(0); run.Start < model.Count; run = model.GetNextRun(run.Start + Math.Max(1, run.Length))) {
            yield return run;
         }
      }

      private void CopyAddressExecute(IFileSystem fileSystem) {
         fileSystem.CopyText = scroll.ViewPointToDataIndex(selection.SelectionStart).ToString("X6");
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
         OnMessage?.Invoke(this, $"'{fileSystem.CopyText}' copied to clipboard.");
      }

      private void CopyBytesExecute(IFileSystem fileSystem) {
         fileSystem.CopyText = GetSelectedByteContents();
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
         OnMessage?.Invoke(this, $"'{fileSystem.CopyText}' copied to clipboard.");
      }

      private void DeepCopyExecute(IFileSystem fileSystem) {
         var selectionStart = scroll.ViewPointToDataIndex(selection.SelectionStart);
         var selectionEnd = scroll.ViewPointToDataIndex(selection.SelectionEnd);
         var left = Math.Min(selectionStart, selectionEnd);
         var length = Math.Abs(selectionEnd - selectionStart) + 1;
         if (length > CopyLimit) {
            OnError?.Invoke(this, $"Cannot copy more than {CopyLimit} bytes at once!");
         } else {
            bool usedHistory = false;
            fileSystem.CopyText = Model.Copy(() => { usedHistory = true; return history.CurrentChange; }, left, length, deep: true);
            RefreshBackingData();
            if (usedHistory) UpdateToolsFromSelection(left);
         }
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
      }

      #endregion

      private void MoveSelectionStartExecuted(object arg, Direction direction) {
         var format = this[SelectionStart.X, SelectionStart.Y].Format;
         if (format is UnderEdit underEdit && underEdit.AutocompleteOptions != null && underEdit.AutocompleteOptions.Count > 0) {
            int index = -1;
            for (int i = 0; i < underEdit.AutocompleteOptions.Count; i++) if (underEdit.AutocompleteOptions[i].IsSelected) index = i;
            var options = default(IReadOnlyList<AutoCompleteSelectionItem>);
            if (direction == Direction.Up) {
               index -= 1;
               if (index < -1) index = underEdit.AutocompleteOptions.Count - 1;
               options = AutoCompleteSelectionItem.Generate(underEdit.AutocompleteOptions.Select(option => option.CompletionText), index);
            } else if (direction == Direction.Down) {
               index += 1;
               if (index == underEdit.AutocompleteOptions.Count) index = -1;
               options = AutoCompleteSelectionItem.Generate(underEdit.AutocompleteOptions.Select(option => option.CompletionText), index);
            }
            if (options != null) {
               var edit = new UnderEdit(underEdit.OriginalFormat, underEdit.CurrentText, underEdit.EditWidth, options);
               currentView[SelectionStart.X, SelectionStart.Y] = new HexElement(this[SelectionStart.X, SelectionStart.Y], edit);
               NotifyCollectionChanged(ResetArgs);
               return;
            }
         }
         PreviewScrollChanged?.Invoke(this, EventArgs.Empty);
         selection.MoveSelectionStart.Execute(arg);
      }

      public Point ConvertAddressToViewPoint(int address) => scroll.DataIndexToViewPoint(address);
      public int ConvertViewPointToAddress(Point p) => scroll.ViewPointToDataIndex(p);

      public IReadOnlyList<IContextItem> GetContextMenuItems(Point selectionPoint) {
         Debug.Assert(IsSelected(selectionPoint));
         var factory = new ContextItemFactory(this);
         var cell = this[SelectionStart.X, SelectionStart.Y];
         cell.Format.Visit(factory, cell.Value);
         var results = factory.Results.ToList();
         if (!SelectionStart.Equals(SelectionEnd)) {
            results.Add(new ContextItem("Copy", Copy.Execute) { ShortcutText = "Ctrl+C" });
            results.Add(new ContextItem("Deep Copy", DeepCopy.Execute) { ShortcutText = "Ctrl+Shift+C" });
         }
         results.Add(new ContextItem("Paste", arg => Edit(((IFileSystem)arg).CopyText)) { ShortcutText = "Ctrl+V" });
         results.Add(new ContextItem("Copy Address", arg => CopyAddressExecute((IFileSystem)arg)));
         return results;
      }

      public bool IsSelected(Point point) => selection.IsSelected(point);

      public bool IsTable(Point point) {
         var search = scroll.ViewPointToDataIndex(point);
         var run = Model.GetNextRun(search);
         return run.Start <= search && run is ITableRun;
      }

      public void Refresh() {
         scroll.DataLength = Model.Count;
         RefreshBackingData();
         using (ModelCacheScope.CreateScope(Model)) {
            Tools.TableTool.DataForCurrentRunChanged();
         }
      }

      public void RaiseError(string text) => OnError?.Invoke(this, text);

      private string deferredMessage;
      public void RaiseMessage(string text) {
         // TODO queue multiple messages.
         deferredMessage = text;
         tools.Schedule(RaiseMessage);
      }
      private void RaiseMessage() => OnMessage?.Invoke(this, deferredMessage);

      public void ClearAnchor() {
         var startDataIndex = scroll.ViewPointToDataIndex(SelectionStart);
         var endDataIndex = scroll.ViewPointToDataIndex(SelectionEnd);
         if (startDataIndex > endDataIndex) (startDataIndex, endDataIndex) = (endDataIndex, startDataIndex);

         // do the clear with a custom token that can't change data.
         // This anchor-clear is a formatting-only change.
         Model.ClearAnchor(history.InsertCustomChange(new NoDataChangeDeltaModel()), startDataIndex, endDataIndex - startDataIndex + 1);
         RefreshBackingData();
      }

      public void Edit(string input, IFileSystem continuation = null) {
         if (!UpdateInProgress) {
            UpdateInProgress = true;
            CurrentProgressScopes.Insert(0, tools.DeferUpdates);
            CurrentProgressScopes.Insert(0, ModelCacheScope.CreateScope(Model));
            initialWorkLoad = input.Length;
         }

         // allow chunking at newline boundaries only
         int chunkSize = Math.Max(200, initialWorkLoad / 100);
         var maxSize = input.Length;

         if (continuation != null && input.Length > chunkSize) {
            var nextNewline = input.Substring(chunkSize).IndexOf('\n');
            if (nextNewline != -1) maxSize = chunkSize + nextNewline + 1;
         }

         exitEditEarly = false;
         try {
            for (int i = 0; i < input.Length && i < maxSize && !exitEditEarly; i++) Edit(input[i]);
         } catch {
            ClearEditWork();
            throw;
         }

         if (input.Length > maxSize) {
            Progress = (double)(initialWorkLoad - input.Length) / initialWorkLoad;
            continuation.DispatchWork(() => Edit(input.Substring(maxSize), continuation));
         } else {
            ClearEditWork();
         }
      }

      private void ClearEditWork() {
         CurrentProgressScopes.ForEach(scope => scope.Dispose());
         CurrentProgressScopes.Clear();
         UpdateInProgress = false;
      }

      public void Edit(ConsoleKey key) {
         using (ModelCacheScope.CreateScope(Model)) {
            var offset = scroll.ViewPointToDataIndex(GetEditPoint());
            var run = Model.GetNextRun(offset);
            var point = GetEditPoint();
            var element = this[point.X, point.Y];
            var underEdit = element.Format as UnderEdit;
            if (key == ConsoleKey.Enter && underEdit != null) {
               if (underEdit.AutocompleteOptions != null && underEdit.AutocompleteOptions.Any(option => option.IsSelected)) {
                  var selectedIndex = AutoCompleteSelectionItem.SelectedIndex(underEdit.AutocompleteOptions);
                  underEdit = new UnderEdit(underEdit.OriginalFormat, underEdit.AutocompleteOptions[selectedIndex].CompletionText, underEdit.EditWidth);
                  currentView[point.X, point.Y] = new HexElement(element.Value, element.Edited, underEdit);
                  RequestMenuClose?.Invoke(this, EventArgs.Empty);
                  TryCompleteEdit(point);
               } else {
                  Edit(Environment.NewLine);
               }
               return;
            }
            if (key == ConsoleKey.Enter && run is ITableRun arrayRun1) {
               var offsets = arrayRun1.ConvertByteOffsetToArrayOffset(offset);
               SilentScroll(offsets.SegmentStart + arrayRun1.ElementLength);
            }
            if (key == ConsoleKey.Tab && run is ITableRun arrayRun2) {
               var offsets = arrayRun2.ConvertByteOffsetToArrayOffset(offset);
               SilentScroll(offsets.SegmentStart + arrayRun2.ElementContent[offsets.SegmentIndex].Length);
            }
            if (key == ConsoleKey.Escape) {
               ClearEdits(SelectionStart);
               ClearMessage?.Invoke(this, EventArgs.Empty);
               RequestMenuClose?.Invoke(this, EventArgs.Empty);
            }

            if (key != ConsoleKey.Backspace) return;
            AcceptBackspace(underEdit, point);
         }
      }

      public void Autocomplete(string input) {
         var point = SelectionStart;
         var element = this[point.X, point.Y];
         var underEdit = element.Format as UnderEdit;
         if (underEdit == null) return;
         var index = underEdit.AutocompleteOptions.Select(option => option.CompletionText).ToList().IndexOf(input);
         underEdit = new UnderEdit(underEdit.OriginalFormat, underEdit.AutocompleteOptions[index].CompletionText, underEdit.EditWidth);
         currentView[point.X, point.Y] = new HexElement(element.Value, element.Edited, underEdit);
         TryCompleteEdit(point);
      }

      public void RepointToNewCopy(int pointer) {
         var destinationAddress = Model.ReadPointer(pointer);
         if (destinationAddress == Pointer.NULL) {
            CreateNewData(pointer);
            return;
         }

         var destination = Model.GetNextRun(destinationAddress);
         if (destination.PointerSources.Count < 2) {
            OnError?.Invoke(this, "This is the only pointer, no need to make a new copy.");
            return;
         }

         if (destination is ArrayRun) {
            OnError?.Invoke(this, "Cannot automatically duplicate a table. This operation is unsafe.");
            return;
         }

         var newDestination = Model.FindFreeSpace(destination.Start, destination.Length);
         if (newDestination == -1) {
            newDestination = Model.Count;
            Model.ExpandData(history.CurrentChange, Model.Count + destination.Length);
         }

         for (int i = 0; i < destination.Length; i++) {
            history.CurrentChange.ChangeData(Model, newDestination + i, Model[destination.Start + i]);
         }

         Model.ClearPointer(CurrentChange, pointer, destination.Start);
         Model.WritePointer(CurrentChange, pointer, newDestination); // point to the new destination
         var destination2 = Model.GetNextRun(destination.Start);
         Model.ObserveRunWritten(CurrentChange, destination2.Duplicate(newDestination, new SortedSpan<int>(pointer))); // create a new run at the new destination
         OnMessage?.Invoke(this, "New Copy added at " + newDestination.ToString("X6"));
         Refresh();
      }

      public void OpenInNewTab(int destination) {
         var child = new ViewPort(FileName, Model, Singletons, history);
         child.selection.GotoAddress(destination);
         RequestTabChange?.Invoke(this, child);
      }

      private bool CreateNewData(int pointer) {
         var errorText = "Can only create new data for a pointer with a format within a table.";
         if (!(Model.GetNextRun(pointer) is ITableRun tableRun)) {
            OnError?.Invoke(this, errorText);
            return false;
         }
         var offsets = tableRun.ConvertByteOffsetToArrayOffset(pointer);
         if (!(tableRun.ElementContent[offsets.SegmentIndex] is ArrayRunPointerSegment pointerSegment) || !pointerSegment.IsInnerFormatValid) {
            OnError?.Invoke(this, errorText);
            return false;
         }

         var length = FormatRunFactory.GetStrategy(pointerSegment.InnerFormat).LengthForNewRun(Model, pointer);

         var insert = Model.FindFreeSpace(0, length);
         if (insert < 0) {
            insert = Model.Count;
            Model.ExpandData(CurrentChange, Model.Count + length);
            scroll.DataLength = Model.Count;
         }
         pointerSegment.WriteNewFormat(Model, CurrentChange, pointer, insert, tableRun.ElementContent);
         RaiseMessage($"New data added at {insert:X6}");
         RefreshBackingData();
         return true;
      }

      private void AcceptBackspace(UnderEdit underEdit, Point point) {
         // backspace in progress with characters left: just clear a character
         if (underEdit != null && underEdit.CurrentText.Length > 0) {
            var newText = underEdit.CurrentText.Substring(0, underEdit.CurrentText.Length - 1);
            var options = underEdit.AutocompleteOptions;
            if (options != null) {
               var selectedIndex = AutoCompleteSelectionItem.SelectedIndex(underEdit.AutocompleteOptions);
               options = GetAutocompleteOptions(underEdit.OriginalFormat, newText, selectedIndex);
            }
            var newFormat = new UnderEdit(underEdit.OriginalFormat, newText, underEdit.EditWidth, options);
            currentView[point.X, point.Y] = new HexElement(this[point.X, point.Y], newFormat);
            NotifyCollectionChanged(ResetArgs);
            return;
         }

         var index = scroll.ViewPointToDataIndex(point);

         // backspace on an empty element: clear the data from those cells
         if (underEdit != null) {
            var operation = new DataClear(Model, history.CurrentChange, index);
            underEdit.OriginalFormat.Visit(operation, Model[index]);
            RefreshBackingData();
            SelectionStart = scroll.DataIndexToViewPoint(index - 1);
            point = GetEditPoint();
            index = scroll.ViewPointToDataIndex(point);
         }

         var run = Model.GetNextRun(index);

         if (run.Start > index) {
            // no run: doing a raw edit.
            SelectionStart = scroll.DataIndexToViewPoint(index);
            var element = this[SelectionStart.X, SelectionStart.Y];
            var text = element.Value.ToString("X2");
            currentView[SelectionStart.X, SelectionStart.Y] = new HexElement(element, element.Format.Edit(text.Substring(0, text.Length - 1)));
            NotifyCollectionChanged(ResetArgs);
            return;
         }

         if (run is PCSRun || run is AsciiRun) {
            for (int i = index; i < run.Start + run.Length; i++) history.CurrentChange.ChangeData(Model, i, 0xFF);
            var length = PCSString.ReadString(Model, run.Start, true);
            if (run is PCSRun) Model.ObserveRunWritten(history.CurrentChange, new PCSRun(Model, run.Start, length, run.PointerSources));
            RefreshBackingData();
            SelectionStart = scroll.DataIndexToViewPoint(index - 1);
            return;
         }

         var cellToText = new ConvertCellToText(Model, run.Start);
         var cell = this[point];

         void TableBackspace(int length) {
            PrepareForMultiSpaceEdit(point, length);
            cell.Format.Visit(cellToText, cell.Value);
            var text = cellToText.Result;
            if (cell.Format is BitArray) {
               for (int i = 1; i < length; i++) {
                  var extraData = Model[scroll.ViewPointToDataIndex(point) + i];
                  cell.Format.Visit(cellToText, extraData);
                  text += cellToText.Result;
               }
            }
            text = text.Substring(0, text.Length - 1);
            currentView[point.X, point.Y] = new HexElement(cell, new UnderEdit(cell.Format, text, length));
         }

         if (run is ITableRun array) {
            var offsets = array.ConvertByteOffsetToArrayOffset(index);
            if (array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.PCS) {
               for (int i = index + 1; i < offsets.SegmentStart + array.ElementContent[offsets.SegmentIndex].Length; i++) history.CurrentChange.ChangeData(Model, i, 0x00);
               history.CurrentChange.ChangeData(Model, index, 0xFF);
               RefreshBackingData();
               SelectionStart = scroll.DataIndexToViewPoint(index - 1);
            } else if (array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.Pointer) {
               TableBackspace(4);
            } else if (array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.Integer) {
               TableBackspace(((Integer)cell.Format).Length);
            } else if (array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.BitArray) {
               TableBackspace(((BitArray)cell.Format).Length);
            } else {
               throw new NotImplementedException();
            }
            NotifyCollectionChanged(ResetArgs);
            return;
         }

         if (run is EggMoveRun || run is PLMRun) {
            PrepareForMultiSpaceEdit(point, 2);
            cell.Format.Visit(cellToText, cell.Value);
            var text = cellToText.Result;
            text = text.Substring(0, text.Length - 1);
            currentView[point.X, point.Y] = new HexElement(cell, new UnderEdit(cell.Format, text, 2));
            NotifyCollectionChanged(ResetArgs);
            return;
         }

         if (run.Start <= index && run.Start + run.Length > index) {
            // I want to do a backspace at the end of this run
            SelectionStart = scroll.DataIndexToViewPoint(run.Start);
            var element = this[SelectionStart.X, SelectionStart.Y];
            element.Format.Visit(cellToText, element.Value);
            var text = cellToText.Result;

            var editLength = 1;
            if (element.Format is Pointer) editLength = 4;

            for (int i = 0; i < run.Length; i++) {
               var p = scroll.DataIndexToViewPoint(run.Start + i);
               string editString = i == 0 ? text.Substring(0, text.Length - 1) : string.Empty;
               if (i > 0) editLength = 1;
               var format = new UnderEdit(this[p.X, p.Y].Format, editString, editLength);
               currentView[p.X, p.Y] = new HexElement(this[p.X, p.Y], format);
            }
         }

         NotifyCollectionChanged(ResetArgs);
      }

      #region Find

      public IReadOnlyList<(int start, int end)> Find(string rawSearch) {
         var results = new List<(int start, int end)>();
         var cleanedSearchString = rawSearch.ToUpper();
         var searchBytes = new List<ISearchByte>();

         // it might be a string with no quotes, we should check for matches for that.
         if (cleanedSearchString.Length > 3 && !cleanedSearchString.Contains(StringDelimeter) && !cleanedSearchString.All(AllHexCharacters.Contains)) {
            results.AddRange(FindUnquotedText(cleanedSearchString, searchBytes));
         }

         // it might be a pointer without angle braces
         if (cleanedSearchString.Length == 6 && cleanedSearchString.All(AllHexCharacters.Contains)) {
            searchBytes.AddRange(Parse(cleanedSearchString).Reverse().Append((byte)0x08).Select(b => (SearchByte)b));
            results.AddRange(Model.Search(searchBytes).Select(result => (result, result + 3)));
         }

         // it might be a bl command
         if (cleanedSearchString.StartsWith("BL ") && cleanedSearchString.Contains("<") && cleanedSearchString.EndsWith(">")) {
            results.AddRange(FindBranchLink(cleanedSearchString));
         }

         // attempt to parse the search string fully
         if (TryParseSearchString(searchBytes, cleanedSearchString, errorOnParseError: results.Count == 0)) {
            // find matches
            results.AddRange(Model.Search(searchBytes).Select(result => (result, result + searchBytes.Count - 1)));
         }

         // reorder the list to start at the current cursor position
         results.Sort((a, b) => a.start.CompareTo(b.start));
         var offset = scroll.ViewPointToDataIndex(SelectionStart);
         var left = results.Where(result => result.start < offset);
         var right = results.Where(result => result.start >= offset);
         results = right.Concat(left).ToList();
         NotifyNumberOfResults(rawSearch, results.Count);
         return results;
      }

      private IEnumerable<(int start, int end)> FindBranchLink(string command) {
         var addressStart = command.IndexOf(" <") + 2;
         var addressEnd = command.LastIndexOf(">");
         if (addressEnd < addressStart) yield break;
         var addressText = command.Substring(addressStart, addressEnd - addressStart);
         if (!int.TryParse(addressText, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out int address)) {
            address = Model.GetAddressFromAnchor(CurrentChange, -1, addressText);
            if (address < 0 || address >= Model.Count) yield break;
         }

         // I want to know, for any given point in the raw data, if it's possible a branch-link command pointing to `address`
         // branch link commands are always 4 bytes and have the following format:
         // 11111 #11 11110 #11, where #=pc+#*2+4
         // note that this command is 4 bytes long, stored byte reversed. So in the data, it's:
         // 8 bits: bits 11-18 of a 22 bit signed offset
         // 8 bits:
         //         the low 3 bits are bits 19-21 of a 22 bit signed offset
         //         the high 5 bits are always 11110
         // 8 bits: bits 0-7 of a 22 bit signed offset
         // 8 bits:
         //         the low 3 bits are bits 8-10 of a 22 bit signed offset
         //         the high 5 bits are always 11111
         // the command is always 2-byte aligned
         //
         // bit order is really weird (11-18, 19-21, 0-7, 8-10) because BL is made of **2** instructions,
         // and each instruction is stored little-endian

         // start as early as possible in the file: maximum offset, or offset for source=0
         int offset = Math.Min(0b0111111111111111111111, (address - 4) / 2);
         for (; true; offset--) { // traveling down the offsets means traveling up the source options
            int source = address - 4 - offset * 2;
            if (source + 4 > Model.RawData.Length) break;
            if (Model.RawData[source + 2] != (byte)offset) continue; // check source+2 first because it's the simplest, and thus fastest
            if (Model.RawData[source + 0] != (byte)(offset >> 11)) continue;
            if (Model.RawData[source + 3] != (0b11111000 | (0b111 & offset >> 8))) continue;
            if (Model.RawData[source + 1] != (0b11110000 | (0b111 & offset >> 19))) continue;
            yield return (source, source + 3);
         }
      }

      private IEnumerable<(int start, int end)> FindUnquotedText(string cleanedSearchString, List<ISearchByte> searchBytes) {
         var pcsBytes = PCSString.Convert(cleanedSearchString);
         pcsBytes.RemoveAt(pcsBytes.Count - 1); // remove the 0xFF that was added, since we're searching for a string segment instead of a whole string.

         // only search for the string if every character in the search string is allowed
         if (pcsBytes.Count != cleanedSearchString.Length) yield break;

         searchBytes.AddRange(pcsBytes.Select(b => new PCSSearchByte(b)));
         var textResults = Model.Search(searchBytes).ToList();
         Model.ConsiderResultsAsTextRuns(history.CurrentChange, textResults);
         foreach (var result in textResults) {
            if (Model.GetNextRun(result) is ArrayRun parentArray && parentArray.LengthFromAnchor == string.Empty) {
               foreach (var dataResult in FindMatchingDataResultsFromArrayElement(parentArray, result)) yield return dataResult;
            }

            yield return (result, result + pcsBytes.Count - 1);
         }
      }

      /// <summary>
      /// When performing a search, sometimes one of the search results is text from a table.
      /// If so, then we also care about places where that table value is used.
      /// This function finds uses of an element in a table.
      /// </summary>
      private IEnumerable<(int start, int end)> FindMatchingDataResultsFromArrayElement(ArrayRun parentArray, int parentIndex) {
         var offsets = parentArray.ConvertByteOffsetToArrayOffset(parentIndex);
         var parentArrayName = Model.GetAnchorFromAddress(-1, parentArray.Start);
         if (offsets.SegmentIndex == 0 && parentArray.ElementContent[offsets.SegmentIndex].Type == ElementContentType.PCS) {
            var arrayUses = FindTableUsages(offsets, parentArrayName);
            var streamUses = FindStreamUsages(offsets, parentArrayName);
            return arrayUses.Concat(streamUses);
         }
         return Enumerable.Empty<(int, int)>();
      }

      private IEnumerable<(int start, int end)> FindTableUsages(ArrayOffset offsets, string parentArrayName) {
         foreach (var child in Model.Arrays) {
            // option 1: another table has a row named after this element
            if (child.LengthFromAnchor == parentArrayName) {
               var address = child.Start + child.ElementLength * offsets.ElementIndex;
               yield return (address, address + child.ElementLength - 1);
            }

            // option 2: another table has an enum named after this element
            var segmentOffset = 0;
            foreach (var segment in child.ElementContent) {
               if (!(segment is ArrayRunEnumSegment enumSegment) || enumSegment.EnumName != parentArrayName) {
                  segmentOffset += segment.Length;
                  continue;
               }
               for (int i = 0; i < child.ElementCount; i++) {
                  var address = child.Start + child.ElementLength * i + segmentOffset;
                  var enumValue = Model.ReadMultiByteValue(address, segment.Length);
                  if (enumValue != offsets.ElementIndex) continue;
                  yield return (address, address + segment.Length - 1);
               }
               segmentOffset += segment.Length;
            }
         }
      }

      private IEnumerable<(int start, int end)> FindStreamUsages(ArrayOffset offsets, string parentArrayName) {
         foreach (var child in Model.Streams) {
            // option 1: the value is used by egg moves
            if (child is EggMoveRun eggRun) {
               foreach (var result in eggRun.Search(parentArrayName, offsets.ElementIndex)) yield return result;
            }
            // option 2: the value is used by learnable moves
            if (child is PLMRun plmRun && parentArrayName == HardcodeTablesModel.MoveNamesTable) {
               foreach (var result in plmRun.Search(offsets.ElementIndex)) yield return result;
            }
            // option 3: the value is a move used by trainer teams
            if (child is TrainerPokemonTeamRun team) {
               foreach (var result in team.Search(parentArrayName, offsets.ElementIndex)) {
                  yield return (result, result + 1);
               }
            }
            // option 3: the value is in an enum used by a custom table stream
            if (child is TableStreamRun table) {
               foreach (var result in table.Search(parentArrayName, offsets.ElementIndex)) yield return result;
            }
         }
      }

      private void NotifyNumberOfResults(string rawSearch, int results) {
         if (results == 1) {
            OnMessage?.Invoke(this, $"Found only 1 match for '{rawSearch}'.");
         } else if (results > 1) {
            OnMessage?.Invoke(this, $"Found {results} matches for '{rawSearch}'.");
         }
      }

      private byte[] Parse(string content) {
         var result = new byte[content.Length / 2];
         for (int i = 0; i < result.Length; i++) {
            var thisByte = content.Substring(i * 2, 2);
            result[i] += (byte)(AllHexCharacters.IndexOf(thisByte[0]) * 0x10);
            result[i] += (byte)AllHexCharacters.IndexOf(thisByte[1]);
         }
         return result;
      }

      private bool TryParseSearchString(List<ISearchByte> searchBytes, string cleanedSearchString, bool errorOnParseError) {
         for (int i = 0; i < cleanedSearchString.Length;) {
            if (cleanedSearchString[i] == ' ') {
               i++;
               continue;
            }

            if (cleanedSearchString[i] == PointerStart) {
               if (!TryParsePointerSearchSegment(searchBytes, cleanedSearchString, ref i)) return false;
               continue;
            }

            if (cleanedSearchString[i] == StringDelimeter) {
               if (TryParseStringSearchSegment(searchBytes, cleanedSearchString, ref i)) continue;
            }

            if (cleanedSearchString.Length >= i + 2 && cleanedSearchString.Substring(i, 2).All(AllHexCharacters.Contains)) {
               searchBytes.AddRange(Parse(cleanedSearchString.Substring(i, 2)).Select(b => (SearchByte)b));
               i += 2;
               continue;
            }

            if (errorOnParseError) OnError(this, $"Could not parse search term {cleanedSearchString.Substring(i)}");
            return false;
         }

         return true;
      }

      private bool TryParsePointerSearchSegment(List<ISearchByte> searchBytes, string cleanedSearchString, ref int i) {
         var pointerEnd = cleanedSearchString.IndexOf(PointerEnd, i);
         if (pointerEnd == -1) { OnError(this, "Search mismatch: no closing >"); return false; }
         var pointerContents = cleanedSearchString.Substring(i + 1, pointerEnd - i - 1);
         var address = Model.GetAddressFromAnchor(history.CurrentChange, -1, pointerContents);
         if (address != Pointer.NULL) {
            searchBytes.Add((SearchByte)(address >> 0));
            searchBytes.Add((SearchByte)(address >> 8));
            searchBytes.Add((SearchByte)(address >> 16));
            searchBytes.Add((SearchByte)0x08);
         } else if (pointerContents.All(AllHexCharacters.Contains) && pointerContents.Length <= 6) {
            searchBytes.AddRange(Parse(pointerContents).Reverse().Append((byte)0x08).Select(b => (SearchByte)b));
         } else {
            OnError(this, $"Could not parse pointer <{pointerContents}>");
            return false;
         }
         i = pointerEnd + 1;
         return true;
      }

      private bool TryParseStringSearchSegment(List<ISearchByte> searchBytes, string cleanedSearchString, ref int i) {
         var endIndex = cleanedSearchString.IndexOf(StringDelimeter, i + 1);
         while (endIndex > i && cleanedSearchString[endIndex - 1] == '\\') endIndex = cleanedSearchString.IndexOf(StringDelimeter, endIndex + 1);
         if (endIndex > i) {
            var pcsBytes = PCSString.Convert(cleanedSearchString.Substring(i, endIndex + 1 - i));
            i = endIndex + 1;
            if (i == cleanedSearchString.Length) pcsBytes.RemoveAt(pcsBytes.Count - 1);
            searchBytes.AddRange(pcsBytes.Select(b => new PCSSearchByte(b)));
            return true;
         } else {
            return false;
         }
      }

      #endregion

      public IChildViewPort CreateChildView(int startAddress, int endAddress) {
         var child = new ChildViewPort(this, Singletons);

         var run = Model.GetNextRun(startAddress);
         if (run is ArrayRun array) {
            var offsets = array.ConvertByteOffsetToArrayOffset(startAddress);
            var lineStart = array.Start + array.ElementLength * offsets.ElementIndex;
            child.Goto.Execute(lineStart.ToString("X2"));
            child.SelectionStart = child.ConvertAddressToViewPoint(startAddress);
            child.SelectionEnd = child.ConvertAddressToViewPoint(endAddress);
         } else {
            child.Goto.Execute(startAddress.ToString("X2"));
            child.SelectionEnd = child.ConvertAddressToViewPoint(endAddress);
         }

         return child;
      }

      public void FollowLink(int x, int y) {
         var format = this[x, y].Format;
         if (format is Anchor anchor) format = anchor.OriginalFormat;

         using (ModelCacheScope.CreateScope(Model)) {
            // follow pointer
            if (format is Pointer pointer) {
               if (pointer.Destination != Pointer.NULL) {
                  selection.GotoAddress(pointer.Destination);
               } else if (string.IsNullOrEmpty(pointer.DestinationName)) {
                  OnError(this, $"null pointers point to nothing, so going to their source isn't possible.");
               } else {
                  OnError(this, $"Pointer destination {pointer.DestinationName} not found.");
               }
               return;
            }

            // follow word value source
            if (format is MatchedWord word) {
               var address = Model.GetAddressFromAnchor(history.CurrentChange, -1, word.Name.Substring(2));
               if (address == Pointer.NULL) {
                  OnError(this, $"No table with name '{word.Name.Substring(2)}' was found.");
               } else {
                  selection.GotoAddress(address);
               }
               return;
            }

            // open tool
            var byteOffset = scroll.ViewPointToDataIndex(new Point(x, y));
            var currentRun = Model.GetNextRun(byteOffset);
            if (currentRun is ISpriteRun) {
               tools.SpriteTool.SpriteAddress = currentRun.Start;
               tools.SelectedIndex = Tools.IndexOf(Tools.SpriteTool);
            } else if (currentRun is IPaletteRun) {
               tools.SpriteTool.PaletteAddress = currentRun.Start;
               tools.SelectedIndex = Tools.IndexOf(Tools.SpriteTool);
            } else if (currentRun is IStreamRun) {
               Tools.StringTool.Address = currentRun.Start;
               Tools.SelectedIndex = Tools.IndexOf(Tools.StringTool);
            } else if (currentRun is ITableRun array) {
               var offsets = array.ConvertByteOffsetToArrayOffset(byteOffset);
               if (format is PCS) {
                  Tools.StringTool.Address = offsets.SegmentStart - offsets.ElementIndex * array.ElementLength;
                  Tools.SelectedIndex = Tools.IndexOf(Tools.StringTool);
               } else {
                  Tools.TableTool.Address = array.Start + offsets.ElementIndex * array.ElementLength;
                  Tools.SelectedIndex = Tools.IndexOf(Tools.TableTool);
               }
            }
         }
      }

      public void ExpandSelection(int x, int y) {
         var index = scroll.ViewPointToDataIndex(SelectionStart);
         var run = Model.GetNextRun(index);
         if (run.Start > index) return;
         if (run is ITableRun array) {
            var offsets = array.ConvertByteOffsetToArrayOffset(index);
            if (array.ElementContent[offsets.SegmentIndex].Type == ElementContentType.Pointer) {
               FollowLink(x, y);
            } else {
               SelectionStart = scroll.DataIndexToViewPoint(offsets.SegmentStart);
               SelectionEnd = scroll.DataIndexToViewPoint(offsets.SegmentStart + array.ElementContent[offsets.SegmentIndex].Length - 1);
            }
         } else if (run is PointerRun) {
            FollowLink(x, y);
         } else if (run is IScriptStartRun xse) {
            var length = tools.CodeTool.ScriptParser.GetScriptSegmentLength(Model, run.Start);
            if (xse is BSERun) length = tools.CodeTool.BattleScriptParser.GetScriptSegmentLength(Model, run.Start);
            SelectionStart = scroll.DataIndexToViewPoint(run.Start);
            SelectionEnd = scroll.DataIndexToViewPoint(run.Start + length - 1);
            tools.CodeTool.Mode = CodeMode.Script;
            if (xse is BSERun) tools.CodeTool.Mode = CodeMode.BattleScript;
            tools.SelectedIndex = tools.IndexOf(tools.CodeTool);
         } else {
            SelectionStart = scroll.DataIndexToViewPoint(run.Start);
            SelectionEnd = scroll.DataIndexToViewPoint(run.Start + run.Length - 1);
         }
      }

      public void ConsiderReload(IFileSystem fileSystem) {
         if (!history.IsSaved) return; // don't overwrite local changes

         try {
            var file = fileSystem.LoadFile(FileName);
            if (file == null) return; // asked to load the file, but the file wasn't found... carry on
            var metadata = fileSystem.MetadataFor(FileName);
            Model.Load(file.Contents, metadata != null ? new StoredMetadata(metadata) : null);
            scroll.DataLength = Model.Count;
            CascadeScripts();
            RefreshBackingData();

            // if the new file is shorter, selection might need to be updated
            // this forces it to be re-evaluated.
            SelectionStart = SelectionStart;
         } catch (IOException) {
            // something happened when we tried to load the file
            // try again soon.
            RequestDelayedWork?.Invoke(this, () => ConsiderReload(fileSystem));
         }
      }

      public virtual void FindAllSources(int x, int y) {
         var anchor = this[x, y].Format as Anchor;
         if (anchor == null) return;
         var title = string.IsNullOrEmpty(anchor.Name) ? (y * Width + x + scroll.DataIndex).ToString("X6") : anchor.Name;
         title = "Sources of " + title;
         var newTab = new SearchResultsViewPort(title);

         foreach (var source in anchor.Sources) newTab.Add(CreateChildView(source, source), source, source);

         RequestTabChange(this, newTab);
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
      }

      public void OpenSearchResultsTab(string title, IReadOnlyList<(int start, int end)> matches) {
         if (matches.Count == 1) {
            var (start, end) = matches[0];
            selection.GotoAddress(start);
            SelectionStart = scroll.DataIndexToViewPoint(start);
            SelectionEnd = scroll.DataIndexToViewPoint(end);
            return;
         }

         var newTab = new SearchResultsViewPort(title);
         foreach (var (start, end) in matches) newTab.Add(CreateChildView(start, end), start, end);
         RequestTabChange(this, newTab);
      }

      public void OpenDexReorderTab(string dexTableName) {
         var newTab = new DexReorderTab(history, Model, dexTableName, HardcodeTablesModel.DexInfoTableName, dexTableName == HardcodeTablesModel.NationalDexTableName);
         RequestTabChange(this, newTab);
      }

      public void ValidateMatchedWords() {
         // TODO if this is too slow, add a method to the model to get the set of only MatchedWordRuns.
         for (var run = Model.GetNextRun(0); run != NoInfoRun.NullRun; run = Model.GetNextRun(run.Start + Math.Max(1, run.Length))) {
            if (!(run is WordRun wordRun)) continue;
            var address = Model.GetAddressFromAnchor(history.CurrentChange, -1, wordRun.SourceArrayName);
            if (address == Pointer.NULL) continue;
            var array = Model.GetNextRun(address) as ArrayRun;
            if (array == null) continue;
            var actualValue = Model.ReadValue(wordRun.Start);
            if (array.ElementCount == actualValue) continue;
            OnMessage?.Invoke(this, $"MatchedWord at {wordRun.Start:X6} was expected to have value {array.ElementCount}, but was {actualValue}.");
            Goto.Execute(wordRun.Start.ToString("X6"));
            break;
         }
      }

      public void InsertPalette16() {
         var selectionPoint = scroll.ViewPointToDataIndex(SelectionStart);
         var run1 = Model.GetNextRun(selectionPoint);
         var run2 = Model.GetNextRun(selectionPoint + 1);
         if (run1.Start + 32 != run2.Start || !(run1 is NoInfoRun)) {
            OnError?.Invoke(this, "Palettes insertion requires a no-format anchor with exactly 32 bytes of space.");
            return;
         }
         for (int i = 0; i < 16; i++) {
            if (Model.ReadMultiByteValue(run1.Start + i * 2, 2) >= 0x8000) {
               OnError?.Invoke(this, $"Palette colors only use 15 bits, but the high bit it set at {run1.Start + i * 2 + 1:X6}.");
               return;
            }
         }
         var currentName = Model.GetAnchorFromAddress(-1, run1.Start);
         if (string.IsNullOrEmpty(currentName)) currentName = $"pal{run1.Start:X6}";
         Model.ObserveAnchorWritten(CurrentChange, currentName, new PaletteRun(run1.Start, new PaletteFormat(4, 1)));
         Refresh();
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
         UpdateToolsFromSelection(run1.Start);
      }

      public void CascadeScript(int address) {
         Width = Math.Max(Width, 16);   // hack to make the width right on initial load
         Height = Math.Max(Height, 16); // hack to make the height right on initial load
         var addressText = address.ToString("X6");
         Goto.Execute(addressText);
         Debug.Assert(scroll.DataIndex == address - address % 16);
         var length = tools.CodeTool.ScriptParser.GetScriptSegmentLength(Model, address);
         Model.ClearFormat(CurrentChange, address, length - 1);

         using (ModelCacheScope.CreateScope(Model)) {
            tools.CodeTool.ScriptParser.FormatScript<XSERun>(CurrentChange, Model, address);
         }

         SelectionStart = scroll.DataIndexToViewPoint(address);
         SelectionEnd = scroll.DataIndexToViewPoint(address + length - 1);
         tools.CodeTool.Mode = CodeMode.Script;
         tools.SelectedIndex = tools.IndexOf(tools.CodeTool);
      }

      private void Edit(char input) {
         var point = GetEditPoint();
         var element = this[point.X, point.Y];

         if (input.IsAny('\r', '\n')) {
            input = ' '; // handle multiline pasting by just treating the newlines as standard whitespace.
            withinComment = false;
            if (element.Format is PCS || element.Format is ErrorPCS) return; // exit early: newlines within strings are ignored, because they're escaped.
         }

         if (element.Format is UnderEdit && input == ',') input = ' ';

         if (!ShouldAcceptInput(point, element, input)) {
            ClearEdits(point);
            return;
         }

         SelectionStart = point;

         if (element == this[point.X, point.Y]) {
            UnderEdit newFormat;
            if (element.Format is UnderEdit underEdit && underEdit.AutocompleteOptions != null) {
               var newText = underEdit.CurrentText + input;
               var autoCompleteOptions = GetAutocompleteOptions(underEdit.OriginalFormat, newText);
               newFormat = new UnderEdit(underEdit.OriginalFormat, newText, underEdit.EditWidth, autoCompleteOptions);
            } else {
               newFormat = element.Format.Edit(input.ToString());
            }
            currentView[point.X, point.Y] = new HexElement(element, newFormat);
         } else {
            // ShouldAcceptInput already did the work: nothing to change
         }

         if (!TryCompleteEdit(point)) {
            // only need to notify collection changes if we didn't complete an edit
            NotifyCollectionChanged(ResetArgs);
         }
      }

      private IReadOnlyList<AutoCompleteSelectionItem> GetAutocompleteOptions(IDataFormat originalFormat, string newText, int selectedIndex = -1) {
         using (ModelCacheScope.CreateScope(Model)) {
            if (originalFormat is Anchor anchor) originalFormat = anchor.OriginalFormat;
            if (newText.StartsWith(PointerStart.ToString())) {
               return Model.GetNewPointerAutocompleteOptions(newText, selectedIndex);
            } else if (newText.StartsWith(GotoMarker.ToString())) {
               return Model.GetNewPointerAutocompleteOptions(newText, selectedIndex);
            } else if (originalFormat is IntegerEnum intEnum) {
               var array = (ITableRun)Model.GetNextRun(intEnum.Source);
               var segment = (ArrayRunEnumSegment)array.ElementContent[array.ConvertByteOffsetToArrayOffset(intEnum.Source).SegmentIndex];
               var options = segment.GetOptions(Model).Select(option => option + " "); // autocomplete needs to complete after selection, so add a space
               return AutoCompleteSelectionItem.Generate(options.Where(option => option.MatchesPartial(newText)), selectedIndex);
            } else if (originalFormat is EggSection || originalFormat is EggItem) {
               var eggRun = (EggMoveRun)Model.GetNextRun(((IDataFormatInstance)originalFormat).Source);
               var allOptions = eggRun.GetAutoCompleteOptions();
               return AutoCompleteSelectionItem.Generate(allOptions.Where(option => option.MatchesPartial(newText)), selectedIndex);
            } else if (originalFormat is PlmItem) {
               if (!newText.Contains(" ")) return AutoCompleteSelectionItem.Generate(Enumerable.Empty<string>(), -1);
               var moveName = newText.Substring(newText.IndexOf(' ')).Trim();
               if (moveName.Length == 0) return AutoCompleteSelectionItem.Generate(Enumerable.Empty<string>(), -1);
               var plmRun = (PLMRun)Model.GetNextRun(((IDataFormatInstance)originalFormat).Source);
               var allOptions = plmRun.GetAutoCompleteOptions(newText.Split(' ')[0]);
               return AutoCompleteSelectionItem.Generate(allOptions.Where(option => option.MatchesPartial(moveName)), selectedIndex);
            } else if (newText.StartsWith(":")) {
               return Model.GetNewWordAutocompleteOptions(newText, selectedIndex);
            } else {
               return null;
            }
         }
      }

      private void ClearEdits(Point point) {
         if (this[point.X, point.Y].Format is UnderEdit) RefreshBackingData();
         withinComment = false;
      }

      private Point GetEditPoint() {
         var selectionStart = scroll.ViewPointToDataIndex(SelectionStart);
         var selectionEnd = scroll.ViewPointToDataIndex(SelectionEnd);
         var leftEdge = Math.Min(selectionStart, selectionEnd);

         var point = scroll.DataIndexToViewPoint(leftEdge);
         scroll.ScrollToPoint(ref point);

         return point;
      }

      private void IsTextExecuted(object notUsed) {
         var selectionStart = scroll.ViewPointToDataIndex(selection.SelectionStart);
         var selectionEnd = scroll.ViewPointToDataIndex(selection.SelectionEnd);
         var left = Math.Min(selectionStart, selectionEnd);
         var length = Math.Abs(selectionEnd - selectionStart) + 1;
         var startPlaces = Model.FindPossibleTextStartingPlaces(left, length);

         // do the actual search now that we know places to start
         var foundCount = Model.ConsiderResultsAsTextRuns(history.CurrentChange, startPlaces);
         if (foundCount == 0) {
            OnError?.Invoke(this, "Failed to automatically find text at that location.");
         } else {
            RefreshBackingData();
         }

         RequestMenuClose?.Invoke(this, EventArgs.Empty);
      }

      private bool ShouldAcceptInput(Point point, HexElement element, char input) {
         var memoryLocation = scroll.ViewPointToDataIndex(point);

         // special cases: if there's no edit unde way, there's a few formats that can be added anywhere. Handle those first.
         if (!(element.Format is UnderEdit)) {
            if (input == ExtendArray) {
               var index = scroll.ViewPointToDataIndex(point);
               if (Model.IsAtEndOfArray(index, out var _)) return true;
            }

            if (input == AnchorStart || input == GotoMarker) {
               // anchor edits are actually 0 length
               // but lets give them 4 spaces to work with
               PrepareForMultiSpaceEdit(point, 4);
               var autoCompleteOptions = input == GotoMarker ? new AutoCompleteSelectionItem[0] : null;
               var underEdit = new UnderEdit(element.Format, input.ToString(), 4, autoCompleteOptions);
               currentView[point.X, point.Y] = new HexElement(element, underEdit);
               return true;
            }

            if (input == CommentStart) {
               var underEdit = new UnderEdit(element.Format, input.ToString());
               currentView[point.X, point.Y] = new HexElement(element, underEdit);
               withinComment = true;
               return true;
            }
         }

         // normal case: the logic for how to handle this edit depends on what format is in this cell.
         var startCellEdit = new StartCellEdit(Model, memoryLocation, input);
         element.Format.Visit(startCellEdit, element.Value);
         if (startCellEdit.NewFormat != null) {
            // if the edit provided a new format, go ahead and build a new element based on that format.
            // if no new format was provided, then the default logic in the method above will make a new UnderEdit cell if the Result is true.
            currentView[point.X, point.Y] = new HexElement(element, startCellEdit.NewFormat);
            if (startCellEdit.NewFormat.EditWidth > 1) PrepareForMultiSpaceEdit(point, startCellEdit.NewFormat.EditWidth);
         }
         return startCellEdit.Result;
      }

      private (Point start, Point end) GetSelectionSpan(Point p) {
         var index = scroll.ViewPointToDataIndex(p);
         var run = Model.GetNextRun(index);
         if (run.Start > index) return (p, p);

         (Point, Point) pair(int start, int end) => (scroll.DataIndexToViewPoint(start), scroll.DataIndexToViewPoint(end));

         using (ModelCacheScope.CreateScope(Model)) {
            if (run.CreateDataFormat(Model, index) is IDataFormatInstance instance) {
               return pair(instance.Source, instance.Source + instance.Length - 1);
            }
         }
         if (!(run is ITableRun array)) return (p, p);

         var naturalEnd = array.Start + array.ElementCount * array.ElementLength;
         if (naturalEnd <= index) {
            return pair(naturalEnd, array.Start + array.Length - 1);
         }

         var offset = array.ConvertByteOffsetToArrayOffset(index);
         var type = array.ElementContent[offset.SegmentIndex].Type;
         if (type == ElementContentType.Pointer || type == ElementContentType.Integer || type == ElementContentType.BitArray) {
            return pair(offset.SegmentStart, offset.SegmentStart + array.ElementContent[offset.SegmentIndex].Length - 1);
         }

         return (p, p);
      }

      private void PrepareForMultiSpaceEdit(Point point, int length) {
         var index = scroll.ViewPointToDataIndex(point);
         var endIndex = index + length - 1;
         for (int i = 1; i < length; i++) {
            point = scroll.DataIndexToViewPoint(index + i);
            if (point.Y >= Height) return;
            var element = this[point.X, point.Y];
            var newFormat = element.Format.Edit(string.Empty);
            currentView[point.X, point.Y] = new HexElement(element, newFormat);
         }
         selection.PropertyChanged -= SelectionPropertyChanged; // don't notify on multi-space edit: it breaks up the undo history
         SelectionEnd = scroll.DataIndexToViewPoint(endIndex);
         selection.PropertyChanged += SelectionPropertyChanged;
      }

      private bool TryCompleteEdit(Point point) {
         // wrap this whole method in an anti-recursion clause
         selection.PreviewSelectionStartChanged -= ClearActiveEditBeforeSelectionChanges;
         using (new StubDisposable { Dispose = () => selection.PreviewSelectionStartChanged += ClearActiveEditBeforeSelectionChanges }) {

            var element = this[point.X, point.Y];
            var underEdit = element.Format as UnderEdit;
            if (underEdit == null) return false; // no edit to complete

            if (TryGeneralCompleteEdit(underEdit.CurrentText, point, out bool result)) {
               return result;
            }

            // normal case: whether or not to accept the edit depends on the existing cell format
            var dataIndex = scroll.ViewPointToDataIndex(point);
            var completeEditOperation = new CompleteCellEdit(Model, dataIndex, underEdit.CurrentText, history.CurrentChange);
            using (ModelCacheScope.CreateScope(Model)) {
               underEdit.OriginalFormat.Visit(completeEditOperation, element.Value);

               if (completeEditOperation.Result) {
                  // if the data we just changed was in a table, notify children of that table about the change
                  if (Model.GetNextRun(dataIndex) is ITableRun tableRun) {
                     var offsets = tableRun.ConvertByteOffsetToArrayOffset(dataIndex);
                     var errorInfo = tableRun.NotifyChildren(Model, history.CurrentChange, offsets.ElementIndex, offsets.SegmentIndex);
                     HandleErrorInfo(errorInfo);
                  }

                  // update the cell / selection
                  if (completeEditOperation.NewCell != null) {
                     currentView[point.X, point.Y] = completeEditOperation.NewCell;
                  }
                  if (completeEditOperation.DataMoved || completeEditOperation.NewDataIndex > scroll.DataLength) scroll.DataLength = Model.Count;

                  // update tools from the new moved selection
                  var run = Model.GetNextRun(completeEditOperation.NewDataIndex);
                  if (run.Start > completeEditOperation.NewDataIndex) run = new NoInfoRun(Model.Count);
                  if (completeEditOperation.DataMoved) UpdateToolsFromSelection(run.Start);
                  if (run is ITableRun) {
                     Tools.Schedule(Tools.TableTool.DataForCurrentRunChanged);
                  }
                  if (run is ITableRun || run is IStreamRun) Tools.Schedule(Tools.StringTool.DataForCurrentRunChanged);
                  if (run is ISpriteRun || run is IPaletteRun) {
                     tools.Schedule(tools.SpriteTool.DataForCurrentRunChanged);
                     tools.Schedule(tools.TableTool.DataForCurrentRunChanged);
                  }
                  if (completeEditOperation.MessageText != null) OnMessage?.Invoke(this, completeEditOperation.MessageText);
                  if (completeEditOperation.ErrorText != null) OnError?.Invoke(this, completeEditOperation.ErrorText);

                  // refresh the screen
                  if (!SilentScroll(completeEditOperation.NewDataIndex) && completeEditOperation.NewCell == null) {
                     RefreshBackingData();
                  }

                  UpdateToolsFromSelection(completeEditOperation.NewDataIndex);
               }
            }

            return completeEditOperation.Result;
         }
      }

      /// <summary>
      /// Some edits are valid no matter where you are in the data.
      /// Try to complete one of those edits here.
      /// Return true if it's a special edit. Result is true if the edit was completed.
      /// </summary>
      private bool TryGeneralCompleteEdit(string currentText, Point point, out bool result) {
         result = false;

         // goto marker
         if (currentText.StartsWith(GotoMarker.ToString())) {
            if (currentText.Length == 2 && currentText[1] == '{') {
               var currentAddress = scroll.ViewPointToDataIndex(point);
               var destination = Model.ReadPointer(currentAddress);
               if (destination == Pointer.NULL) {
                  if (CreateNewData(currentAddress)) {
                     destination = Model.ReadPointer(currentAddress);
                  } else {
                     OnError?.Invoke(this, $"Could not jump using pointer at {currentAddress:X6}");
                  }
               }
               ClearEdits(point);
               if (destination >= 0 && destination < Model.Count) {
                  Goto.Execute(destination);
                  selection.SetJumpBackPoint(currentAddress + 4);
               } else if (destination != Pointer.NULL) {
                  OnError?.Invoke(this, $"Could not jump using pointer at {currentAddress:X6}");
               }
               RequestMenuClose?.Invoke(this, EventArgs.Empty);
               result = true;
            } else if (currentText.Length == 2 && currentText[1] == '}') {
               ClearEdits(point);
               selection.Back.Execute();
               RequestMenuClose?.Invoke(this, EventArgs.Empty);
               result = true;
            }
            if (char.IsWhiteSpace(currentText[currentText.Length - 1])) {
               var destination = currentText.Substring(1);
               ClearEdits(point);
               Goto.Execute(destination);
               RequestMenuClose?.Invoke(this, EventArgs.Empty);
               result = true;
            }

            return true;
         }

         // comment
         if (currentText.StartsWith(CommentStart.ToString())) {
            if (currentText.Length > 1 && currentText.EndsWith(CommentStart.ToString())) withinComment = false;
            result = (currentText.EndsWith(" ") || currentText.EndsWith("#")) && !withinComment;
            if (result) ClearEdits(point);
            return true;
         }

         // anchor start
         if (currentText.StartsWith(AnchorStart.ToString())) {
            TryUpdate(ref anchorText, currentText, nameof(AnchorText));
            var endingCharacter = currentText[currentText.Length - 1];
            // anchor format will only end once the user
            // -> types a whitespace character,
            // -> types a closing quote for the text format ""
            // -> types a closing ` for a `` format
            if (!char.IsWhiteSpace(endingCharacter) && !currentText.EndsWith(AsciiRun.StreamDelimeter.ToString())) {
               AnchorTextVisible = true;
               return true;
            }

            // special case: `asc` has a length token outside the ``, so the anchor isn't completed if it ends with `asc`
            if (currentText.EndsWith("`asc`")) {
               AnchorTextVisible = true;
               return true;
            }

            // only end the anchor edit if the [] brace count matches
            if (currentText.Sum(c => c == '[' ? 1 : c == ']' ? -1 : 0) != 0) {
               AnchorTextVisible = true;
               return true;
            }

            // only end the anchor if the "" and `` quote count are even
            if (currentText.Count(AsciiRun.StreamDelimeter) % 2 != 0 || currentText.Count(StringDelimeter) % 2 != 0) {
               AnchorTextVisible = true;
               return true;
            }

            if (!CompleteAnchorEdit(point)) exitEditEarly = true;
            result = true;
            return true;
         }

         // table extension
         var dataIndex = scroll.ViewPointToDataIndex(point);
         if (currentText == ExtendArray.ToString() && Model.IsAtEndOfArray(dataIndex, out var arrayRun)) {
            var originalArray = arrayRun;
            var errorInfo = Model.CompleteArrayExtension(history.CurrentChange, 1, ref arrayRun);
            if (!errorInfo.HasError || errorInfo.IsWarning) {
               if (arrayRun != null && arrayRun.Start != originalArray.Start) {
                  ScrollFromTableMove(dataIndex, originalArray, arrayRun);
               }
               RefreshBackingData();
               SelectionEnd = GetSelectionSpan(SelectionStart).end;
            }
            HandleErrorInfo(errorInfo);
            result = true;
            return true;
         }

         return false;
      }

      /// <returns>True if it was completed successfully, false if some sort of error occurred and we should abort the remainder of the edit.</returns>
      private bool CompleteAnchorEdit(Point point) {
         var underEdit = (UnderEdit)this[point.X, point.Y].Format;
         var index = scroll.ViewPointToDataIndex(point);
         ErrorInfo errorInfo;

         // if it's an unnamed text/stream anchor, we have special logic for that
         using (ModelCacheScope.CreateScope(Model)) {
            if (underEdit.CurrentText.Trim() == AnchorStart + PCSRun.SharedFormatString) {
               int count = Model.ConsiderResultsAsTextRuns(history.CurrentChange, new[] { index });
               if (count == 0) {
                  errorInfo = new ErrorInfo("An anchor with nothing pointing to it must have a name.");
               } else {
                  errorInfo = ErrorInfo.NoError;
               }
            } else if (underEdit.CurrentText == AnchorStart + PLMRun.SharedFormatString) {
               if (!PokemonModel.ConsiderAsPlmStream(Model, index, history.CurrentChange)) {
                  errorInfo = new ErrorInfo("An anchor with nothing pointing to it must have a name.");
               } else {
                  errorInfo = ErrorInfo.NoError;
                  Tools.StringTool.RefreshContentAtAddress();
               }
            } else if (underEdit.CurrentText == AnchorStart + XSERun.SharedFormatString) {
               // TODO
               CascadeScript(index);
               errorInfo = ErrorInfo.NoError;
            } else {
               errorInfo = PokemonModel.ApplyAnchor(Model, history.CurrentChange, index, underEdit.CurrentText);
               Tools.StringTool.RefreshContentAtAddress();
            }
         }

         ClearEdits(point);
         UpdateToolsFromSelection(index);

         if (errorInfo == ErrorInfo.NoError) {
            if (Model.GetNextRun(index) is ArrayRun array && array.Start == index) Goto.Execute(index.ToString("X2"));
            return true;
         }

         HandleErrorInfo(errorInfo);

         return errorInfo.IsWarning;
      }

      private void ScrollFromTableMove(int initialSelection, ITableRun oldRun, ITableRun newRun) {
         scroll.DataLength = Model.Count; // possible length change
         var tableOffset = scroll.DataIndex - oldRun.Start;
         var relativeSelection = initialSelection - oldRun.Start;
         selection.PropertyChanged -= SelectionPropertyChanged;
         selection.GotoAddress(newRun.Start + tableOffset);
         selection.SelectionStart = scroll.DataIndexToViewPoint(newRun.Start + relativeSelection);
         selection.PropertyChanged += SelectionPropertyChanged;
      }

      private bool SilentScroll(int memoryLocation) {
         var nextPoint = scroll.DataIndexToViewPoint(memoryLocation);
         var didScroll = true;
         if (!scroll.ScrollToPoint(ref nextPoint)) {
            // only need to notify collection change if we didn't auto-scroll after changing cells
            NotifyCollectionChanged(ResetArgs);
            didScroll = false;
         }

         UpdateSelectionWithoutNotify(nextPoint);
         return didScroll;
      }

      private void HandleErrorInfo(ErrorInfo info) {
         if (!info.HasError) return;
         if (info.IsWarning) OnMessage?.Invoke(this, info.ErrorMessage);
         else OnError?.Invoke(this, info.ErrorMessage);
      }

      /// <summary>
      /// When automatically updating the selection,
      /// update it without notifying ourselves.
      /// This lets us tell the difference between a manual cell change and an auto-cell change,
      /// which is useful for deciding change history boundaries.
      /// </summary>
      private void UpdateSelectionWithoutNotify(Point nextPoint) {
         selection.PropertyChanged -= SelectionPropertyChanged;

         SelectionStart = nextPoint;
         NotifyPropertyChanged(nameof(SelectionStart));
         NotifyPropertyChanged(nameof(SelectionEnd));

         selection.PropertyChanged += SelectionPropertyChanged;
      }

      private void ModelChangedByTool(object sender, IFormattedRun run) {
         if (run.Start < scroll.ViewPointToDataIndex(new Point(Width - 1, Height - 1)) || run.Start + run.Length > scroll.DataIndex) {
            // there's some visible data that changed
            RefreshBackingData();
         }

         if (run is ITableRun && sender != Tools.StringTool && Model.GetNextRun(Tools.StringTool.Address).Start == run.Start) Tools.StringTool.DataForCurrentRunChanged();
         if (run is ITableRun && Model.GetNextRun(Tools.TableTool.Address).Start == run.Start) Tools.TableTool.DataForCurrentRunChanged();
      }

      private void ModelDataMovedByTool(object sender, (int originalLocation, int newLocation) locations) {
         scroll.DataLength = Model.Count;
         if (scroll.DataIndex <= locations.originalLocation && locations.originalLocation < scroll.ViewPointToDataIndex(new Point(Width - 1, Height - 1))) {
            // data was moved from onscreen: follow it
            int offset = locations.originalLocation - scroll.DataIndex;
            selection.GotoAddress(locations.newLocation - offset);
         }
         RaiseMessage($"Data was automatically moved to {locations.newLocation:X6}. Pointers were updated.");
      }

      private void ModelChangedByCodeTool(object sender, ErrorInfo e) {
         RefreshBackingData();
         HandleErrorInfo(e);
      }

      private void RefreshBackingData(Point p) {
         var index = scroll.ViewPointToDataIndex(p);
         var edited = Model.HasChanged(index);
         if (index < 0 | index >= Model.Count) { currentView[p.X, p.Y] = HexElement.Undefined; return; }
         var run = Model.GetNextRun(index);
         if (index < run.Start) { currentView[p.X, p.Y] = new HexElement(Model[index], edited, None.Instance); return; }
         var format = run.CreateDataFormat(Model, index);
         format = Model.WrapFormat(run, format, index);
         currentView[p.X, p.Y] = new HexElement(Model[index], edited, format);
      }

      private void RefreshBackingData() {
         currentView = new HexElement[Width, Height];
         RequestMenuClose?.Invoke(this, EventArgs.Empty);
         NotifyCollectionChanged(ResetArgs);
         NotifyPropertyChanged(nameof(FreeSpaceStart));
      }

      private void RefreshBackingDataFull() {
         currentView = new HexElement[Width, Height];
         IFormattedRun run = null;
         using (ModelCacheScope.CreateScope(Model)) {
            for (int y = 0; y < Height; y++) {
               for (int x = 0; x < Width; x++) {
                  var index = scroll.ViewPointToDataIndex(new Point(x, y));
                  var edited = Model.HasChanged(index);
                  if (run == null || index >= run.Start + run.Length) {
                     run = Model.GetNextRun(index) ?? new NoInfoRun(Model.Count);
                  }
                  if (index < 0 || index >= Model.Count) {
                     currentView[x, y] = HexElement.Undefined;
                  } else if (index >= run.Start) {
                     var format = run.CreateDataFormat(Model, index);
                     format = Model.WrapFormat(run, format, index);
                     currentView[x, y] = new HexElement(Model[index], edited, format);
                  } else {
                     currentView[x, y] = new HexElement(Model[index], edited, None.Instance);
                  }
               }
            }
         }
      }

      private void UpdateColumnHeaders() {
         var index = scroll.ViewPointToDataIndex(new Point(0, 0));
         var run = Model.GetNextRun(index) as ArrayRun;
         if (run != null && run.Start > index) run = null; // only use the run if it starts _before_ the screen
         var headers = run?.GetColumnHeaders(Width, index) ?? HeaderRow.GetDefaultColumnHeaders(Width, index);

         for (int i = 0; i < headers.Count; i++) {
            if (i < ColumnHeaders.Count) ColumnHeaders[i] = headers[i];
            else ColumnHeaders.Add(headers[i]);
         }

         while (ColumnHeaders.Count > headers.Count) ColumnHeaders.RemoveAt(ColumnHeaders.Count - 1);
      }

      private void NotifyCollectionChanged(NotifyCollectionChangedEventArgs args) => CollectionChanged?.Invoke(this, args);
   }
}
