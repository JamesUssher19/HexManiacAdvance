﻿using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using static HavenSoft.HexManiac.Core.ICommandExtensions;

namespace HavenSoft.HexManiac.Core.ViewModels.Visitors {
   public interface IContextItem {
      string Text { get; }
      ICommand Command { get; }
      object Parameter { get; }
      string ShortcutText { get; }
   }

   public class ContextItem : IContextItem {
      public string Text { get; }
      public ICommand Command { get; }
      public object Parameter { get; }
      public string ShortcutText { get; set; }
      public ContextItem(string text, Action<object> action, object parameter = null) {
         Text = text;
         Command = action == null ? null : new StubCommand { CanExecute = CanAlwaysExecute, Execute = action };
         Parameter = parameter;
      }
   }

   public class CompositeContextItem : List<IContextItem>, IContextItem {
      public string Text => throw new NotImplementedException();
      public ICommand Command => throw new NotImplementedException();
      public object Parameter => throw new NotImplementedException();
      public string ShortcutText => throw new NotImplementedException();

      public CompositeContextItem(Action<object> command, params string[] text) {
         foreach (var item in text) {
            Add(new ContextItem(item, command, item));
         }
      }
   }

   public class ContextItemFactory : IDataFormatVisitor {
      public ViewPort ViewPort { get; }
      public List<IContextItem> Results { get; } = new List<IContextItem>();

      public ContextItemFactory(ViewPort parent) => ViewPort = parent;

      public void Visit(Undefined dataFormat, byte data) => Visit((None)null, data);

      public void Visit(None dataFormat, byte data) {
         Results.Add(new ContextItem("Display as Text", ViewPort.IsText.Execute));
      }

      public void Visit(UnderEdit dataFormat, byte data) { }

      public void Visit(Pointer pointer, byte data) {
         var point = ViewPort.SelectionStart;
         Results.Add(new ContextItem("Follow Pointer", arg => ViewPort.FollowLink(point.X, point.Y)) { ShortcutText = "Ctrl+Click" });

         var pointerAddress = ViewPort.ConvertViewPointToAddress(point);
         var pointerDestination = ViewPort.Model.ReadPointer(pointerAddress);
         if (pointerDestination == Pointer.NULL) {
            Results.Add(new ContextItem("Create New Data", arg => ViewPort.RepointToNewCopy(pointerAddress)));
         } else {
            var destination = ViewPort.Model.GetNextRun(pointerDestination);
            if (!(destination is NoInfoRun)) {
               Results.Add(new ContextItem("Repoint to New Copy", arg => ViewPort.RepointToNewCopy(pointerAddress)));
            }
            Results.Add(new ContextItem("Open in New Tab", arg => ViewPort.OpenInNewTab(pointerDestination)));
         }

         if (ViewPort.Model.GetNextRun(pointerAddress) is ITableRun arrayRun && arrayRun.Start <= pointerAddress) {
            Results.AddRange(GetTableChildren());
         } else {
            Results.AddRange(GetFormattedChildren());
         }
      }

      public void Visit(Anchor anchor, byte data) {
         if (!string.IsNullOrEmpty(anchor.Name)) {
            Results.Add(new ContextItem(anchor.Name, null));
         };

         if (anchor.Sources.Count == 0) {
            Results.Add(new ContextItem("(Nothing points to this.)", null));
         }

         if (anchor.Sources.Count > 1) {
            Results.Add(new ContextItem("Show All Sources in new tab", p => {
               ViewPort.FindAllSources(ViewPort.SelectionStart.X, ViewPort.SelectionStart.Y);
            }));
         }

         var destinations = anchor.Sources.Select(source => source.ToString("X6")).ToArray();
         if (anchor.Sources.Count < 5) {
            foreach (var destination in destinations) {
               Results.Add(new ContextItem(destination, ViewPort.Goto.Execute, destination));
            }
         } else {
            Results.Add(new CompositeContextItem(ViewPort.Goto.Execute, destinations));
         }

         anchor.OriginalFormat.Visit(this, data);

         if (anchor.OriginalFormat is None) {
            Results.Add(new ContextItem("View as 16-color palette", p => ViewPort.InsertPalette16()));
            Results.AddRange(GetFormattedChildren());
         }
      }

      public void Visit(PCS pcs, byte data) {
         var point = ViewPort.SelectionStart;
         Results.Add(new ContextItem("Open In Text Tool", arg => ViewPort.FollowLink(point.X, point.Y)) { ShortcutText = "Ctrl+Click" });

         var address = ViewPort.ConvertViewPointToAddress(point);
         if (ViewPort.Model.GetNextRun(address) is ITableRun arrayRun && arrayRun.Start <= address) {
            Results.AddRange(GetTableChildren());
         } else {
            Results.AddRange(GetFormattedChildren());
         }
      }

      public void Visit(EscapedPCS pcs, byte data) => Visit((PCS)null, data);

      public void Visit(ErrorPCS pcs, byte data) => Visit((PCS)null, data);

      public void Visit(Ascii ascii, byte data) => Results.AddRange(GetFormattedChildren());

      public void Visit(Integer integer, byte data) {
         if (ViewPort.Model.GetNextRun(ViewPort.Tools.TableTool.Address) is ITableRun) {
            Results.AddRange(GetTableChildren());
         } else {
            Results.AddRange(GetFormattedChildren());
         }
      }

      public void Visit(IntegerEnum integer, byte data) => Results.AddRange(GetTableChildren());

      public void Visit(IntegerHex integerHex, byte data) => Results.AddRange(GetTableChildren());

      public void Visit(EggSection section, byte data) => Visit((EggItem)null, data);

      public void Visit(EggItem item, byte data) {
         var point = ViewPort.SelectionStart;
         Results.Add(new ContextItem("Open In Text Tool", arg => ViewPort.FollowLink(point.X, point.Y)) { ShortcutText = "Ctrl+Click" });
         Results.AddRange(GetFormattedChildren());
      }

      public void Visit(PlmItem item, byte data) {
         var point = ViewPort.SelectionStart;
         Results.Add(new ContextItem("Open In Text Tool", arg => ViewPort.FollowLink(point.X, point.Y)) { ShortcutText = "Ctrl+Click" });
         Results.AddRange(GetFormattedChildren());
      }

      public void Visit(BitArray array, byte data) => Results.AddRange(GetTableChildren());

      public void Visit(MatchedWord word, byte data) => Results.AddRange(GetFormattedChildren());

      public void Visit(EndStream endStream, byte data) => Results.AddRange(GetFormattedChildren());

      public void Visit(UncompressedPaletteColor color, byte data) => Results.AddRange(GetFormattedChildren());

      private IEnumerable<IContextItem> GetTableChildren() {
         if (ViewPort.Tools.TableTool.Append.CanExecute(null)) {
            yield return new ContextItem("Extend Table", ViewPort.Tools.TableTool.Append.Execute);
         }

         yield return new ContextItem("Open in Table Tool", arg => ViewPort.Tools.SelectedIndex = 1);
         foreach (var item in GetFormattedChildren()) yield return item;
      }

      private IEnumerable<IContextItem> GetFormattedChildren() {
         yield return new ContextItem("Clear Format", arg => ViewPort.ClearAnchor());
      }

      public void Visit(LzMagicIdentifier lz, byte data) => VisitLzFormat();

      public void Visit(LzGroupHeader lz, byte data) => VisitLzFormat();

      public void Visit(LzCompressed lz, byte data) => VisitLzFormat();

      public void Visit(LzUncompressed lz, byte data) => VisitLzFormat();

      private void VisitLzFormat() => Results.AddRange(GetFormattedChildren());
   }
}
