﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public enum CodeMode { Thumb, Script, BattleScript, Raw }

   public class CodeTool : ViewModelCore, IToolViewModel {
      public string Name => "Code Tool";

      private string content;
      private CodeMode mode;
      private readonly ThumbParser thumb;
      private readonly ScriptParser script, battleScript;
      private readonly IDataModel model;
      private readonly Selection selection;
      private readonly ChangeHistory<ModelDelta> history;

      public event EventHandler<ErrorInfo> ModelDataChanged;

      public bool IsReadOnly => Mode == CodeMode.Raw;
      public bool UseSingleContent => !UseMultiContent;
      public bool UseMultiContent => Mode.IsAny(CodeMode.Script, CodeMode.BattleScript);

      private bool showErrorText;
      public bool ShowErrorText { get => showErrorText; private set => TryUpdate(ref showErrorText, value); }

      private string errorText;
      public string ErrorText { get => errorText; private set => TryUpdate(ref errorText, value); }

      public CodeMode Mode {
         get => mode;
         set {
            if (!TryUpdateEnum(ref mode, value)) return;
            UpdateContent();
            NotifyPropertyChanged(nameof(IsReadOnly));
            NotifyPropertyChanged(nameof(UseSingleContent));
            NotifyPropertyChanged(nameof(UseMultiContent));
         }
      }

      bool ignoreContentUpdates;
      public string Content {
         get => content;
         set {
            if (ignoreContentUpdates) return;
            TryUpdate(ref content, value);
            CompileChanges();
         }
      }

      public ObservableCollection<CodeBody> Contents { get; } = new ObservableCollection<CodeBody>();

      public ThumbParser Parser => thumb;

      public ScriptParser ScriptParser => script;

      public ScriptParser BattleScriptParser => battleScript;

      public event EventHandler<(int originalLocation, int newLocation)> ModelDataMoved;

      public CodeTool(Singletons singletons, IDataModel model, Selection selection, ChangeHistory<ModelDelta> history) {
         thumb = new ThumbParser(singletons);
         script = new ScriptParser(singletons.ScriptLines, 0x02);
         battleScript = new ScriptParser(singletons.BattleScriptLines, 0x3D);
         script.CompileError += ObserveCompileError;
         battleScript.CompileError += ObserveCompileError;
         this.model = model;
         this.selection = selection;
         this.history = history;
         selection.PropertyChanged += (sender, e) => {
            if (e.PropertyName == nameof(selection.SelectionEnd)) {
               UpdateContent();
            }
         };
      }

      public void DataForCurrentRunChanged() { }

      public void UpdateContent() {
         if (ignoreContentUpdates) return;
         var start = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionStart));
         var end = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionEnd));

         if (start > end) (start, end) = (end, start);
         int length = end - start + 1;

         using (ModelCacheScope.CreateScope(model)) {
            if (length > 0x1000) {
               Content = "Too many bytes selected.";
            } else if (mode == CodeMode.Raw) {
               Content = RawParse(model, start, end - start + 1);
            } else if (length < 2 && mode != CodeMode.Script) {
               TryUpdate(ref content, string.Empty, nameof(Content));
               UpdateContents(-1, null);
            } else if (mode == CodeMode.Script) {
               UpdateContents(start, script);
            } else if (mode == CodeMode.BattleScript) {
               UpdateContents(start, battleScript);
            } else if (mode == CodeMode.Thumb) {
               TryUpdate(ref content, thumb.Parse(model, start, end - start + 1), nameof(Content));
            } else {
               throw new NotImplementedException();
            }
         }
      }

      /// <summary>
      /// Update all the content objects.
      /// If one of the content objects is the one being changed, don't update that one.
      /// </summary>
      /// <param name="start"></param>
      /// <param name="currentScriptStart"></param>
      private void UpdateContents(int start, ScriptParser parser, int currentScriptStart = -1) {
         var scripts = parser?.CollectScripts(model, start) ?? new List<int>();
         for (int i = 0; i < scripts.Count; i++) {
            var scriptStart = scripts[i];
            if (scriptStart == currentScriptStart && Contents.Count > i && Contents[i].Address == scriptStart) continue;
            var scriptLength = parser.FindLength(model, scriptStart);
            var label = scriptStart.ToString("X6");
            var content = parser.Parse(model, scriptStart, scriptLength);
            var body = new CodeBody { Address = scriptStart, Label = label, Content = content };

            if (Contents.Count > i) {
               Contents[i].ContentChanged -= ScriptChanged;
               Contents[i].HelpSourceChanged -= UpdateScriptHelpFromLine;
               Contents[i].Content = body.Content;
               Contents[i].Address = body.Address;
               Contents[i].Label = body.Label;
               Contents[i].HelpSourceChanged += UpdateScriptHelpFromLine;
               Contents[i].ContentChanged += ScriptChanged;
            } else {
               body.ContentChanged += ScriptChanged;
               body.HelpSourceChanged += UpdateScriptHelpFromLine;
               Contents.Add(body);
            }
         }

         while (Contents.Count > scripts.Count) {
            Contents[Contents.Count - 1].ContentChanged -= ScriptChanged;
            Contents.RemoveAt(Contents.Count - 1);
         }
      }

      private void ScriptChanged(object viewModel, EventArgs e) {
         var parser = mode == CodeMode.Script ? script : battleScript;
         var body = (CodeBody)viewModel;
         var codeContent = body.Content;

         var run = model.GetNextRun(body.Address);
         if (run != null && run.Start != body.Address) run = null;

         int length = parser.FindLength(model, body.Address);
         using (ModelCacheScope.CreateScope(model)) {
            if (mode == CodeMode.Script) {
               CompileScriptChanges<XSERun>(body.Address, run, length, ref codeContent, parser, body == Contents[0]);
            }
            else {
               CompileScriptChanges<BSERun>(body.Address, run, length, ref codeContent, parser, body == Contents[0]);
            }

            body.ContentChanged -= ScriptChanged;
            body.HelpSourceChanged -= UpdateScriptHelpFromLine;
            body.Content = codeContent;
            body.HelpSourceChanged += UpdateScriptHelpFromLine;
            body.ContentChanged += ScriptChanged;

            // reload
            var start = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionStart));
            var end = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionEnd));
            if (start > end) (start, end) = (end, start);
            UpdateContents(start, parser, body.Address);
         }
      }

      private void UpdateScriptHelpFromLine(object sender, string line) {
         var codeBody = (CodeBody)sender;
         string help = null;
         if (mode == CodeMode.Script) help = ScriptParser.GetHelp(line);
         else if (mode == CodeMode.BattleScript) BattleScriptParser.GetHelp(line);
         else throw new NotImplementedException();
         codeBody.HelpContent = help;
      }

      private void CompileChanges() {
         using (ModelCacheScope.CreateScope(model)) {
            if (mode == CodeMode.Thumb) CompileThumbChanges();
         }
      }

      private void CompileThumbChanges() {
         var start = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionStart));
         var end = Math.Min(model.Count - 1, selection.Scroll.ViewPointToDataIndex(selection.SelectionEnd));
         if (start > end) (start, end) = (end, start);
         int length = end - start + 1;
         var code = thumb.Compile(model, start, Content.Split(Environment.NewLine));

         if (code.Count != length) return;

         for (int i = 0; i < code.Count; i++) {
            history.CurrentChange.ChangeData(model, start + i, code[i]);
         }

         ModelDataChanged?.Invoke(this, ErrorInfo.NoError);
      }

      private void CompileScriptChanges<TSERun>(int start, IFormattedRun run, int length, ref string codeContent, ScriptParser parser, bool updateSelection) where TSERun : IScriptStartRun {
         ShowErrorText = false;
         ErrorText = string.Empty;
         var sources = run?.PointerSources ?? null;

         ignoreContentUpdates = true;
         {
            var oldScripts = parser.CollectScripts(model, start);
            var code = parser.Compile(history.CurrentChange, model, start, ref codeContent, out var movedData);
            if (code == null) {
               ignoreContentUpdates = false;
               return;
            }

            if (code.Length > length) {
               if (run == null) {
                  var availableLength = length;
                  for (int i = start + length; i < start + code.Length; i++) {
                     if (model[i] == 0xFF) {
                        availableLength++;
                        continue;
                     }
                     ErrorText = $"Script is {code.Length} bytes long, but only {availableLength} bytes are available.";
                     ShowErrorText = true;
                     return;
                  }
               } else {
                  run = (IScriptStartRun)model.RelocateForExpansion(history.CurrentChange, run, code.Length);
                  if (start != run.Start) {
                     ModelDataMoved?.Invoke(this, (start, run.Start));
                     start = run.Start;
                     sources = run.PointerSources;
                  }
               }
            }

            for (int i = 0; i < code.Length; i++) history.CurrentChange.ChangeData(model, start + i, code[i]);
            model.ClearFormatAndData(history.CurrentChange, start + code.Length, length - code.Length);
            parser.FormatScript<TSERun>(history.CurrentChange, model, start, sources);
            if (sources != null) {
               foreach (var source in sources) {
                  var existingRun = model.GetNextRun(source);
                  if (existingRun.Start > source || !(existingRun is ITableRun)) {
                     model.ObserveRunWritten(history.CurrentChange, new PointerRun(source));
                  }
               }
            }

            // this change may have orphaned some existing scripts. Don't lose them!
            var newScripts = parser.CollectScripts(model, start);
            foreach (var orphan in oldScripts.Except(newScripts)) {
               var orphanRun = model.GetNextRun(orphan);
               if (orphanRun.Start == orphan && string.IsNullOrEmpty(model.GetAnchorFromAddress(-1, orphan))) {
                  parser.FormatScript<TSERun>(history.CurrentChange, model, orphan);
                  if (typeof(TSERun) == typeof(XSERun)) {
                     model.ObserveAnchorWritten(history.CurrentChange, $"xse{orphan:X6}", new XSERun(orphan));
                  } else if (typeof(TSERun) == typeof(BSERun)) {
                     model.ObserveAnchorWritten(history.CurrentChange, $"bse{orphan:X6}", new BSERun(orphan));
                  } else {
                     throw new NotImplementedException();
                  }
               }
            }

            if (updateSelection) {
               selection.SelectionStart = selection.Scroll.DataIndexToViewPoint(start);
               selection.SelectionEnd = selection.Scroll.DataIndexToViewPoint(start + code.Length - 1);
            }

            foreach (var movedResource in movedData) ModelDataMoved?.Invoke(this, movedResource);
         }
         ignoreContentUpdates = false;

         ModelDataChanged?.Invoke(this, ErrorInfo.NoError);
      }

      private string RawParse(IDataModel model, int start, int length) {
         var builder = new StringBuilder();
         while (length > 0) {
            builder.Append(model[start].ToHexString());
            builder.Append(" ");
            length--;
            start++;
            if (start % 16 == 0) builder.AppendLine();
         }
         return builder.ToString();
      }

      private void ObserveCompileError(object sender, string error) {
         ShowErrorText = true;
         ErrorText += error + Environment.NewLine;
      }
   }

   public class CodeBody : ViewModelCore {
      public event EventHandler ContentChanged;

      public event EventHandler<string> HelpSourceChanged;

      private string label;
      public string Label {
         get => label;
         set => TryUpdate(ref label, value);
      }

      private int address;
      public int Address {
         get => address;
         set => TryUpdate(ref address, value);
      }

      private int caretPosition;
      public int CaretPosition {
         get => caretPosition;
         set {
            if (!TryUpdate(ref caretPosition, value)) return;
            var lines = content.Split(Environment.NewLine).ToList();
            var contentBoundaryCount = 0;
            while (caretPosition > lines[0].Length) {
               if (lines[0].Trim() == "{") contentBoundaryCount += 1;
               if (lines[0].Trim() == "}") contentBoundaryCount -= 1;
               caretPosition -= lines[0].Length + Environment.NewLine.Length;
               lines.RemoveAt(0);
            }

            // only show help if we're not within content curlies.
            if (contentBoundaryCount != 0) HelpContent = string.Empty;
            else HelpSourceChanged?.Invoke(this, lines[0]);
         }
      }

      private string content;
      public string Content {
         get => content;
         set {
            if (!TryUpdate(ref content, value)) return;
            ContentChanged?.Invoke(this, EventArgs.Empty);
         }
      }

      private string helpContent;
      public string HelpContent { get => helpContent; set => TryUpdate(ref helpContent, value); }
   }
}
