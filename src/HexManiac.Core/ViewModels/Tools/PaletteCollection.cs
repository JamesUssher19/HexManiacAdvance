﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Packaging;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class PaletteCollection : ViewModelCore {
      private readonly ViewPort viewPort;
      private readonly ChangeHistory<ModelDelta> history;

      private int sourcePalette;
      public int SourcePalette { get => sourcePalette; set => Set(ref sourcePalette, value); }
      public ObservableCollection<SelectableColor> Elements { get; } = new ObservableCollection<SelectableColor>();

      public int ColorWidth => (int)Math.Ceiling(Math.Sqrt(Elements.Count));
      public int ColorHeight => (int)Math.Sqrt(Elements.Count);

      private int selectionStart;
      public int SelectionStart {
         get => selectionStart;
         set {
            var first = Math.Min(selectionStart, selectionEnd);
            var last = Math.Max(selectionStart, selectionEnd);
            if (first <= value && value <= last) {
               for (int i = 0; i < Elements.Count; i++) Elements[i].Selected = first <= i && i <= last;
               return;
            }
            if (!TryUpdate(ref selectionStart, value)) return;
            SelectionEnd = selectionStart;
            if (selectionStart == -1) history.ChangeCompleted();
         }
      }

      private int selectionEnd;
      public int SelectionEnd {
         get => selectionEnd;
         set {
            TryUpdate(ref selectionEnd, value);
            var first = Math.Min(selectionStart, selectionEnd);
            var last = Math.Max(selectionStart, selectionEnd);
            for (int i = 0; i < Elements.Count; i++) Elements[i].Selected = first <= i && i <= last;
            createGradient?.CanExecuteChanged.Invoke(createGradient, EventArgs.Empty);
         }
      }

      private int page;
      public int Page { get => page; set => Set(ref page, value); }

      private bool hasMultiplePages;
      public bool HasMultiplePages { get => hasMultiplePages; set => Set(ref hasMultiplePages, value); }

      private StubCommand copy;
      public ICommand Copy => StubCommand<IFileSystem>(ref copy, ExecuteCopy, CanExecuteCopy);

      private StubCommand paste;
      public ICommand Paste => StubCommand<IFileSystem>(ref paste, ExecutePaste, CanExecutePaste);

      private StubCommand createGradient;
      public ICommand CreateGradient => StubCommand(ref createGradient, ExecuteCreateGradient, CanExecuteCreateGradient);

      public event EventHandler<int> RequestPageSet;

      public PaletteCollection(ViewPort viewPort, ChangeHistory<ModelDelta> history) {
         this.viewPort = viewPort;
         this.history = history;
      }

      public IList<(int index, int direction)> HandleMove(int originalIndex, int newIndex) {
         var otherMovedElements = new List<(int, int)>();
         if (originalIndex == newIndex) return otherMovedElements;

         var first = Math.Min(selectionStart, selectionEnd);
         var last = Math.Max(selectionStart, selectionEnd);
         newIndex = Math.Max(newIndex, originalIndex - first);
         newIndex = Math.Min(newIndex, Elements.Count - 1 - last + originalIndex);
         var targetElements = Enumerable.Range(first, last - first + 1).ToList();

         if (originalIndex < newIndex) targetElements.Reverse();
         foreach (var index in targetElements) {
            var element = Elements[index];
            Elements.RemoveAt(index);
            Elements.Insert(newIndex - originalIndex + index, element);
         }

         for (int i = first; i < newIndex - (originalIndex - first); i++) {
            otherMovedElements.Add((i, last - first + 1));
         }
         for (int i = last; i > newIndex + (last - originalIndex); i--) {
            otherMovedElements.Add((i, first - last - 1));
         }

         selectionStart += newIndex - originalIndex;
         SelectionEnd += newIndex - originalIndex;
         return otherMovedElements;
      }

      public void CompleteCurrentInteraction() {
         ReorderPalette();
         history.ChangeCompleted();
      }

      public void SetContents(IReadOnlyList<short> colors) {
         Elements.Clear();
         foreach (var element in Enumerable.Range(0, colors.Count)
            .Select(i => new SelectableColor { Color = colors[i], Index = i })) {
            Elements.Add(element);
         }
         NotifyPropertyChanged(nameof(ColorWidth));
         NotifyPropertyChanged(nameof(ColorHeight));
      }

      public void PushColorsToModel() {
         var model = viewPort.Model;
         if (!(model.GetNextRun(sourcePalette) is IPaletteRun source)) return;

         // update model
         var newPalette = source;
         newPalette = newPalette.SetPalette(model, history.CurrentChange, page, Elements.Select(e => e.Color).ToList());
         if (source.Start != newPalette.Start) viewPort.RaiseMessage($"Palette was moved to {newPalette.Start:X6}. Pointers were updated.");

         // update UI
         var selectionRange = (selectionStart, selectionEnd);
         Refresh();
         (SelectionStart, SelectionEnd) = selectionRange;
      }

      private void ReorderPalette() {
         var model = viewPort.Model;
         if (!(model.GetNextRun(sourcePalette) is IPaletteRun source)) return;

         var oldToNew = Enumerable.Range(0, Elements.Count).Select(i => Elements.IndexOf(Elements.Single(element => element.Index == i))).ToArray();
         var newElements = Enumerable.Range(0, Elements.Count).Select(i => new SelectableColor {
            Index = i,
            Color = Elements[Elements[i].Index].Color,
            Selected = Elements[i].Selected,
         }).ToList();

         // early exit if there's no actual changes
         if (Enumerable.Range(0, Elements.Count).All(i => Elements[i].Index == newElements[i].Index)) return;

         var palettesToUpdate = new List<IPaletteRun> { source };
         foreach (var sprite in source.FindDependentSprites(model).Distinct()) {
            var newSprite = sprite;

            // TODO this doesn't currently work for tilemaps with multiple palettes, such as the first-person-views
            for (int page = 0; page < newSprite.Pages; page++) {
               if (hasMultiplePages) page += this.page;
               var pixels = newSprite.GetPixels(model, page % newSprite.Pages);
               for (int y = 0; y < pixels.GetLength(1); y++) {
                  for (int x = 0; x < pixels.GetLength(0); x++) {
                     pixels[x, y] = oldToNew[pixels[x, y]];
                  }
               }
               newSprite = newSprite.SetPixels(model, history.CurrentChange, page % newSprite.Pages, pixels);
               if (hasMultiplePages) break;
            }

            if (newSprite.Start != sprite.Start) viewPort.RaiseMessage($"Sprite was moved to {newSprite.Start:X6}. Pointers were updated.");
            palettesToUpdate.AddRange(newSprite.FindRelatedPalettes(model));
         }

         foreach (var palette in palettesToUpdate.Distinct()) {
            var newPalette = palette;
            var colors = newPalette.GetPalette(model, page);
            var newColors = Enumerable.Range(0, Elements.Count).Select(i => colors[Elements[i].Index]).ToList();
            newPalette = newPalette.SetPalette(model, history.CurrentChange, page, newColors);
            if (palette.Start != newPalette.Start) viewPort.RaiseMessage($"Palette was moved to {newPalette.Start:X6}. Pointers were updated.");
         }

         Refresh();
         for (int i = 0; i < Elements.Count; i++) Elements[i].Selected = newElements[i].Selected;
      }

      private void Refresh() {
         var currentPage = page;
         viewPort.Refresh();
         if (hasMultiplePages) RequestPageSet?.Invoke(this, currentPage);
      }

      #region Commands

      private void ExecuteCopy(IFileSystem fileSystem) {
         var start = Math.Min(selectionStart, selectionEnd) + 1;
         var end = Math.Max(selectionStart, selectionEnd) + 1;
         var result = UncompressedPaletteColor.Convert(Elements[start - 1].Color);
         for (int i = start; i < end; i++) {
            result += " " + UncompressedPaletteColor.Convert(Elements[i].Color);
         }
         fileSystem.CopyText = result;
      }

      private bool CanExecuteCopy(IFileSystem fileSystem) => 0 <= selectionStart && selectionStart < Elements.Count;

      private static IReadOnlyList<short> ParseColor(string stream) {
         var results = new List<short>();
         var parts = stream.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
         for (int i = 0; i < parts.Length; i++) {
            if (parts[i].Contains(":")) {
               var channels = parts[i].Split(':');
               if (channels.Length != 3) return null;
               if (!int.TryParse(channels[0], out var red) || !int.TryParse(channels[1], out var green) || !int.TryParse(channels[2], out var blue)) return null;
               results.Add(UncompressedPaletteColor.Pack(red, green, blue));
            } else if (parts[i].Length == 4) {
               if (!short.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var color)) return null;
               results.Add(color);
            } else if (parts[i].Length == 2 && i + 1 < parts.Length && parts[i + 1].Length == 2) {
               if (!byte.TryParse(parts[i + 0], NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var low)) return null;
               if (!byte.TryParse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var high)) return null;
               i += 1;
               results.Add((byte)((high << 8) | low));
            } else {
               return null;
            }
         }
         return results;
      }

      private void ExecutePaste(IFileSystem fileSystem) {
         var model = viewPort.Model;
         if (!(model.GetNextRun(sourcePalette) is IPaletteRun)) return;

         // paste data into elements
         var colors = ParseColor(fileSystem.CopyText);
         if (colors == null) return;
         var start = Math.Min(selectionStart, selectionEnd);
         for (int i = 0; i < colors.Count; i++) {
            Elements[start].Color = colors[i];
            if (start < Elements.Count - 1) start += 1;
         }

         PushColorsToModel();
         SelectionStart = start;
      }

      private bool CanExecutePaste(IFileSystem fileSystem) => CanExecuteCopy(fileSystem) && ParseColor(fileSystem.CopyText) != null;

      private void ExecuteCreateGradient() {
         var left = Math.Min(SelectionStart, SelectionEnd);
         var (r, g, b) = UncompressedPaletteColor.ToRGB(Elements[left].Color);
         var leftHSB = Theme.ToHSB((byte)(r << 3), (byte)(g << 3), (byte)(b << 3));

         var right = Math.Max(SelectionStart, SelectionEnd);
         var rightRGB = UncompressedPaletteColor.ToRGB(Elements[right].Color);
         var rightHSB = Theme.ToHSB((byte)(rightRGB.r << 3), (byte)(rightRGB.g << 3), (byte)(rightRGB.b << 3));

         var deltaHue = rightHSB.hue - leftHSB.hue;
         var deltaSat = rightHSB.sat - leftHSB.sat;
         var deltaBright = rightHSB.bright - leftHSB.bright;

         var distance = right - left;
         for (int i = 1; i < distance; i++) {
            var part = (double)i / distance;
            var hue = leftHSB.hue + deltaHue * part;
            var sat = leftHSB.sat + deltaSat * part;
            var bright = leftHSB.bright + deltaBright * part;
            var (red, green, blue) = Theme.FromHSB(hue, sat, bright);
            Elements[left + i].Color = UncompressedPaletteColor.Pack(red >> 3, green >> 3, blue >> 3);
         }

         PushColorsToModel();
      }

      private bool CanExecuteCreateGradient() => Elements.Count(element => element.Selected) > 2;

      #endregion
   }

   [DebuggerDisplay("{Index}:{Color}")]
   public class SelectableColor : ViewModelCore {
      private bool selected;
      public bool Selected { get => selected; set => Set(ref selected, value); }

      private short color;
      public short Color { get => color; set => Set(ref color, value); }

      private int index;
      public int Index { get => index; set => Set(ref index, value); }
   }
}
