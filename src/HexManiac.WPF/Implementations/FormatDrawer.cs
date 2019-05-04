﻿using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.WPF.Controls;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace HavenSoft.HexManiac.WPF.Implementations {
   public class FormatDrawer : IDataFormatVisitor {
      public static int FontSize = 16;

      public static readonly Point CellTextOffset = new Point(6, 1);

      private static readonly List<FormattedText> noneVisualCache = new List<FormattedText>();

      private readonly int modelWidth, modelHeight;

      private readonly DrawingContext context;
      private readonly Geometry rectangleGeometry = new RectangleGeometry(new Rect(new Point(0, 0), new Point(HexContent.CellWidth, HexContent.CellHeight)));

      public bool MouseIsOverCurrentFormat { get; set; }

      public HavenSoft.HexManiac.Core.Models.Point Position { get; set; }

      public FormatDrawer(DrawingContext drawingContext, int width, int height) => (context, modelWidth, modelHeight) = (drawingContext, width, height);

      public static void ClearVisualCaches() {
         noneVisualCache.Clear();
      }

      public void Visit(Undefined dataFormat, byte data) {
         // intentionally draw nothing
      }

      public void Visit(None dataFormat, byte data) {
         VerifyNoneVisualCache();
         context.DrawText(noneVisualCache[data], CellTextOffset);
      }

      public void Visit(UnderEdit dataFormat, byte data) {
         var brush = Brush(nameof(Theme.Primary));
         var typeface = new Typeface("Consolas");

         var content = dataFormat.CurrentText;

         var text = new FormattedText(
            content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush,
            1.0);

         var offset = CellTextOffset;
         var widthOverflow = text.Width - HexContent.CellWidth * dataFormat.EditWidth;
         if (widthOverflow > 0) {
            // make it right aligned
            offset.X -= widthOverflow;
            context.PushClip(new RectangleGeometry(new Rect(new Size(HexContent.CellWidth * dataFormat.EditWidth, HexContent.CellHeight))));
            context.DrawText(text, new Point(-widthOverflow, CellTextOffset.Y));
            context.Pop();
         } else {
            context.DrawText(text, CellTextOffset);
         }
      }

      public void Visit(Pointer dataFormat, byte data) {
         var brush = Brush(nameof(Theme.Accent));
         if (dataFormat.Destination < 0) brush = Brush(nameof(Theme.Error));
         Underline(brush, dataFormat.Position == 0, dataFormat.Position == 3);

         var typeface = new Typeface("Consolas");
         var destination = dataFormat.DestinationAsText;
         if (destination.Length > 13) destination = destination.Substring(0, 11) + "…>";
         var xOffset = 51 - (dataFormat.Position * HexContent.CellWidth) - destination.Length * 4.2; // centering
         var text = new FormattedText(
            destination,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush,
            1.0);

         if (dataFormat.Position > Position.X || Position.X - dataFormat.Position > modelWidth - 4) {
            context.PushClip(rectangleGeometry);
            context.DrawText(text, new Point(CellTextOffset.X + xOffset, CellTextOffset.Y));
            context.Pop();
         } else if (dataFormat.Position == 2) {
            context.DrawText(text, new Point(CellTextOffset.X + xOffset, CellTextOffset.Y));
         }
      }

      private static readonly Geometry Triangle = Geometry.Parse("M0,5 L3,0 6,5");
      public void Visit(Anchor anchor, byte data) {
         anchor.OriginalFormat.Visit(this, data);
         var pen = new Pen(Brush(nameof(Theme.Accent)), 1);
         if (MouseIsOverCurrentFormat) pen.Thickness = 2;
         context.DrawGeometry(null, pen, Triangle);
      }

      public void Visit(PCS pcs, byte data) {
         var typeface = new Typeface("Consolas");
         var text = new FormattedText(
            pcs.ThisCharacter,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brush(nameof(Theme.Text1)),
            1.0);

         var xOffset = 1 - pcs.ThisCharacter.Length;
         context.DrawText(text, new Point(CellTextOffset.X + xOffset, CellTextOffset.Y));
      }

      public void Visit(EscapedPCS pcs, byte data) {
         // intentionally draw nothing: this is taken care of by Visit PCS
      }

      public void Visit(ErrorPCS pcs, byte data) {
         var brush = Brush(nameof(Theme.Error));
         var typeface = new Typeface("Consolas");

         var content = data.ToString("X2");

         var text = new FormattedText(
            content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush,
            1.0);

         context.DrawText(text, CellTextOffset);
      }

      public void Visit(Ascii ascii, byte data) {
         var typeface = new Typeface("Consolas");
         var text = new FormattedText(
            ascii.ThisCharacter.ToString(),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brush(nameof(Theme.Text2)),
            1.0);

         context.DrawText(text, CellTextOffset);
      }

      public void Visit(Integer integer, byte data) {
         if (integer.Position != 0) return;

         var stringValue = integer.Value.ToString();

         var typeface = new Typeface("Consolas");
         var text = new FormattedText(
            stringValue,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brush(nameof(Theme.Data1)),
            1.0);

         var xOffset = CellTextOffset.X;
         xOffset += HexContent.CellWidth / 2 * (integer.Length - 1); // adjust based on number of cells to use
         xOffset -= (stringValue.Length - 2) * 5; // adjust based on width of text
         context.DrawText(text, new Point(xOffset, CellTextOffset.Y));
      }

      public void Visit(IntegerEnum integerEnum, byte data) {
         if (integerEnum.Position != 0) return;

         var stringValue = integerEnum.Value;

         var typeface = new Typeface("Consolas");
         var text = new FormattedText(
            stringValue,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize * 3 / 4,
            Brush(nameof(Theme.Data2)),
            1.0);

         var xOffset = CellTextOffset.X / 2;
         context.PushClip(new RectangleGeometry(new Rect(0, 0, HexContent.CellWidth * integerEnum.Length, HexContent.CellHeight)));
         context.DrawText(text, new Point(xOffset, CellTextOffset.Y));
         context.Pop();
      }

      private void Underline(Brush brush, bool isStart, bool isEnd) {
         int startPoint = isStart ? 5 : 0;
         int endPoint = (int)HexContent.CellWidth - (isEnd ? 5 : 0);
         double y = (int)HexContent.CellHeight - 1.5;
         context.DrawLine(new Pen(brush, 1), new Point(startPoint, y), new Point(endPoint, y));
      }

      private void VerifyNoneVisualCache() {
         if (noneVisualCache.Count != 0) return;

         var bytesAsHex = Enumerable.Range(0, 0x100).Select(i => i.ToString("X2"));

         var text = bytesAsHex.Select(hex => {
            var brush = Brush(nameof(Theme.Primary));
            var typeface = new Typeface("Consolas");
            if (hex == "00" || hex == "FF") brush = Brush(nameof(Theme.Secondary));
            if (hex == "FF") {
               typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Italic, FontWeights.Light, FontStretches.Normal);
            }
            return new FormattedText(
               hex,
               CultureInfo.CurrentCulture,
               FlowDirection.LeftToRight,
               typeface,
               FontSize,
               brush,
               1.0);
         });

         noneVisualCache.AddRange(text);
      }

      private static SolidColorBrush Brush(string name) {
         return (SolidColorBrush)Application.Current.Resources.MergedDictionaries[0][name];
      }
   }
}