﻿// Data Formats are simple types that provide limited meta-data that can vary based on the format.
// Data Formats use the Visitor design pattern to allow things like rendering of the data

using HavenSoft.HexManiac.Core.Models.Runs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.ViewModels.DataFormats {
   public interface IDataFormat : IEquatable<IDataFormat> {
      void Visit(IDataFormatVisitor visitor, byte data);
   }

   public interface IDataFormatInstance : IDataFormat {
      int Source { get; }    // the beginning of the format group that this instance belongs to
      int Position { get; }  // the index within the format group that this instance belongs to
      int Length { get; }    // how long this specific data format is, in bytes
   }

   public interface IDataFormatStreamInstance : IDataFormat {
      int Source { get; }    // the start of the stream
      int Position { get; }  // the index within the stream that this instance belongs to
   }

   public interface IDataFormatVisitor {
      void Visit(Undefined dataFormat, byte data);
      void Visit(None dataFormat, byte data);
      void Visit(UnderEdit dataFormat, byte data);
      void Visit(Pointer pointer, byte data);
      void Visit(Anchor anchor, byte data);
      void Visit(PCS pcs, byte data);
      void Visit(EscapedPCS pcs, byte data);
      void Visit(ErrorPCS pcs, byte data);
      void Visit(Ascii ascii, byte data);
      void Visit(Integer integer, byte data);
      void Visit(IntegerEnum integer, byte data);
      void Visit(IntegerHex integer, byte data);
      void Visit(EggSection section, byte data);
      void Visit(EggItem item, byte data);
      void Visit(PlmItem item, byte data);
      void Visit(BitArray array, byte data);
      void Visit(MatchedWord word, byte data);
      void Visit(EndStream stream, byte data);
      void Visit(LzMagicIdentifier lz, byte data);
      void Visit(LzGroupHeader lz, byte data);
      void Visit(LzCompressed lz, byte data);
      void Visit(LzUncompressed lz, byte data);
      void Visit(UncompressedPaletteColor color, byte data);
   }

   /// <summary>
   /// Used for locations where there is no data.
   /// As in the location is out of range of the file.
   /// </summary>
   public class Undefined : IDataFormat {
      public static Undefined Instance { get; } = new Undefined();
      private Undefined() { }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
      public bool Equals(IDataFormat format) => format is Undefined;
   }

   /// <summary>
   /// Used for locations where the format is unknown, or the data is unused.
   /// Basically everything is 'None' unless we have special information about it.
   /// </summary>
   public class None : IDataFormat {
      public static None Instance { get; } = new None();
      private None() { }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
      public bool Equals(IDataFormat format) => format is None;
   }

   public class UnderEdit : IDataFormat {
      public IDataFormat OriginalFormat { get; }
      public string CurrentText { get; }
      public int EditWidth { get; }
      public IReadOnlyList<AutoCompleteSelectionItem> AutocompleteOptions { get; }
      public UnderEdit(IDataFormat original, string text, int editWidth = 1, IReadOnlyList<AutoCompleteSelectionItem> autocompleteOptions = null) {
         OriginalFormat = original;
         CurrentText = text;
         EditWidth = editWidth;
         AutocompleteOptions = autocompleteOptions;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
      public bool Equals(IDataFormat format) {
         if (!(format is UnderEdit that)) return false;

         if (!OriginalFormat.Equals(that.OriginalFormat)) return false;
         if (EditWidth != that.EditWidth) return false;
         if (AutocompleteOptions != null ^ that.AutocompleteOptions != null) return false; // if only one is null, not equal
         if (AutocompleteOptions != null && that.AutocompleteOptions != null && AutocompleteOptions.SequenceEqual(that.AutocompleteOptions)) return false;
         return CurrentText == that.CurrentText;
      }
   }
   public static class UnderEditExtensions {
      public static UnderEdit Edit(this IDataFormat format, string text) {
         if (format is UnderEdit underEdit) {
            return new UnderEdit(underEdit.OriginalFormat, underEdit.CurrentText + text, underEdit.EditWidth);
         }

         return new UnderEdit(format, text);
      }
   }

   public class Pointer : IDataFormatInstance {
      public const int NULL = -0x08000000;
      public int Source { get; }      // 6 hex digits
      public int Position { get; }    // 0 through 3
      public int Destination { get; } // 6 hex digits
      public int Length => 4;
      public bool HasError { get; }
      public string DestinationName { get; } // null if there is no name for that anchor
      public string DestinationAsText {
         get {
            var destination = DestinationName;
            if (string.IsNullOrEmpty(destination)) destination = Destination.ToString("X6");
            return $"<{destination}>";
         }
      }

      public Pointer(int source, int positionInPointer, int destination, string destinationName, bool hasError) {
         Source = source;
         Position = positionInPointer;
         Destination = destination;
         DestinationName = destinationName;
         HasError = hasError;
      }

      public bool Equals(IDataFormat other) {
         if (!(other is Pointer pointer)) return false;
         return pointer.Source == Source && pointer.Position == Position && pointer.Destination == Destination;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class Anchor : IDataFormat {
      public IDataFormat OriginalFormat { get; }
      public string Name { get; }
      public string Format { get; }
      public IReadOnlyList<int> Sources { get; }

      public Anchor(IDataFormat original, string name, string format, IReadOnlyList<int> sources) => (OriginalFormat, Name, Format, Sources) = (original, name, format, sources);

      public bool Equals(IDataFormat other) {
         if (!(other is Anchor anchor)) return false;
         return anchor.Name == Name && anchor.Format == Format && anchor.Sources.SequenceEqual(Sources) && anchor.OriginalFormat.Equals(OriginalFormat);
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class PCS : IDataFormatStreamInstance {
      public int Source { get; }
      public int Position { get; }
      public string FullString { get; }
      public string ThisCharacter { get; }

      public PCS(int source, int position, string full, string character) => (Source, Position, FullString, ThisCharacter) = (source, position, full, character);

      public bool Equals(IDataFormat other) {
         if (!(other is PCS pcs)) return false;
         return pcs.Source == Source && pcs.Position == Position && pcs.FullString == FullString && pcs.ThisCharacter == ThisCharacter;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class EscapedPCS : IDataFormatStreamInstance {
      public int Source { get; }
      public int Position { get; }
      public string FullString { get; }
      public byte ThisValue { get; }

      public EscapedPCS(int source, int position, string full, byte value) => (Source, Position, FullString, ThisValue) = (source, position, full, value);

      public bool Equals(IDataFormat other) {
         if (!(other is EscapedPCS pcs)) return false;
         return pcs.Source == Source && pcs.Position == Position && pcs.FullString == FullString && pcs.ThisValue == ThisValue;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class ErrorPCS : IDataFormatStreamInstance {
      public int Source { get; }
      public int Position { get; }
      public string FullString { get; }
      public byte ThisValue { get; }

      public ErrorPCS(int source, int position, string full, byte value) => (Source, Position, FullString, ThisValue) = (source, position, full, value);

      public bool Equals(IDataFormat other) {
         if (!(other is EscapedPCS pcs)) return false;
         return pcs.Source == Source && pcs.Position == Position && pcs.FullString == FullString && pcs.ThisValue == ThisValue;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class Ascii : IDataFormatStreamInstance {
      public int Source { get; }
      public int Position { get; }
      public char ThisCharacter { get; }

      public Ascii(int source, int position, char value) => (Source, Position, ThisCharacter) = (source, position, value);

      public bool Equals(IDataFormat other) {
         if (!(other is Ascii ascii)) return false;
         return ascii.Source == Source && ascii.Position == Position && ascii.ThisCharacter == ThisCharacter;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class Integer : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Value { get; }
      public int Length { get; } // number of bytes used by this integer

      public Integer(int source, int position, int value, int length) => (Source, Position, Value, Length) = (source, position, value, length);

      public virtual bool Equals(IDataFormat other) {
         if (!(other is Integer that)) return false;
         return Source == that.Source && Position == that.Position && Value == that.Value && Length == that.Length;
      }

      public virtual bool CanStartWithCharacter(char input) {
         return char.IsNumber(input);
      }

      public virtual void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class IntegerEnum : Integer {
      public new string Value { get; }
      public string DisplayValue {
         get {
            var value = Value;
            if (!value.Contains("_")) return value;
            var display = value.Substring(0, 1);
            while (value.Contains("_")) {
               value = value.Substring(value.IndexOf("_") + 1);
               if (value.Length > 0) display += value.Substring(0, 1);
            }
            return display;
         }
      }

      public IntegerEnum(int source, int position, string value, int length) : base(source, position, -1, length) => Value = value;

      public override bool Equals(IDataFormat other) {
         if (!(other is IntegerEnum that)) return false;
         return Value == that.Value && base.Equals(other);
      }

      public override bool CanStartWithCharacter(char input) {
         return char.IsLetterOrDigit(input) ||
            input == PCSRun.StringDelimeter ||
            "?-".Contains(input);
      }

      public override void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class IntegerHex : Integer {
      public IntegerHex(int source, int position, int value, int length) : base(source, position, value, length) { }

      public override bool Equals(IDataFormat other) {
         if (!(other is IntegerHex)) return false;
         return base.Equals(other);
      }

      public override bool CanStartWithCharacter(char input) => ViewPort.AllHexCharacters.Contains(input);

      public override void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);

      public override string ToString() {
         var format = "X" + (Length * 2);
         return Value.ToString(format);
      }
   }

   public class EggSection : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Length => 2;
      public string SectionName { get; }

      public EggSection(int source, int position, string name) => (Source, Position, SectionName) = (source, position, name);

      public bool Equals(IDataFormat other) {
         if (other is EggSection that) {
            return that.SectionName == SectionName &&
               that.Source == Source &&
               that.Length == Length;
         }
         return false;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class EggItem : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public string ItemName { get; }
      public int Length => 2;

      public EggItem(int source, int position, string name) => (Source, Position, ItemName) = (source, position, name);

      public bool Equals(IDataFormat other) {
         if (other is EggItem that) {
            return that.ItemName == ItemName && that.Source == Source;
         }
         return false;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class PlmItem : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Length => 2;
      public int Level { get; }
      public int Move { get; }
      public string MoveName { get; }
      public override string ToString() => (Level == 0x7F && Move == 0x1FF) ? EggMoveRun.GroupStart + string.Empty + EggMoveRun.GroupEnd : $"{Level} {MoveName}";

      public PlmItem(int source, int position, int level, int move, string moveName) {
         (Source, Position) = (source, position);
         (Level, Move, MoveName) = (level, move, moveName);
      }

      public bool Equals(IDataFormat other) => ToString() == other.ToString();

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class BitArray : IDataFormatStreamInstance {
      public static readonly string SharedFormatString = "|b[]";

      public int Source { get; }
      public int Position { get; }
      public int Length { get; }

      public BitArray(int source, int position, int length) => (Source, Position, Length) = (source, position, length);

      public bool Equals(IDataFormat other) {
         if (!(other is BitArray that)) return false;
         return Source == that.Source && Position == that.Position;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) {
         visitor.Visit(this, data);
      }
   }

   public class MatchedWord : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public string Name { get; }
      public int Length => 4;

      public MatchedWord(int source, int position, string name) => (Source, Position, Name) = (source, position, name);

      public bool Equals(IDataFormat other) {
         if (!(other is MatchedWord that)) return false;
         return Source == that.Source && Position == that.Position;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) {
         visitor.Visit(this, data);
      }
   }

   public class EndStream : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Length { get; }

      public EndStream(int source, int position, int length) => (Source, Position, Length) = (source, position, length);

      public bool Equals(IDataFormat other) {
         if (!(other is EndStream that)) return false;
         return (Source, Position, Length) == (that.Source, that.Position, that.Length);
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class LzMagicIdentifier : IDataFormat {
      public static LzMagicIdentifier Instance { get; } = new LzMagicIdentifier();
      private LzMagicIdentifier() { }
      public bool Equals(IDataFormat other) => other is LzMagicIdentifier;
      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class LzGroupHeader : IDataFormat {
      public static LzGroupHeader Instance { get; } = new LzGroupHeader();
      private LzGroupHeader() { }
      public bool Equals(IDataFormat other) => other is LzMagicIdentifier;
      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class LzUncompressed : IDataFormat {
      public static LzUncompressed Instance { get; } = new LzUncompressed();
      private LzUncompressed() { }
      public bool Equals(IDataFormat other) => other is LzMagicIdentifier;
      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class LzCompressed : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Length => 2;
      public int RunLength { get; }
      public int RunOffset { get; }

      public LzCompressed(int source, int position, int runLength, int runOffset) => (Source, Position, RunLength, RunOffset) = (source, position, runLength, runOffset);

      public bool Equals(IDataFormat other) {
         if (!(other is LzCompressed that)) return false;
         return Source == that.Source && Position == that.Position && RunLength == that.RunLength && RunOffset == that.RunOffset;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);
   }

   public class UncompressedPaletteColor : IDataFormatInstance {
      public int Source { get; }
      public int Position { get; }
      public int Length => 2;
      public short Color { get; }

      public int R { get; }
      public int G { get; }
      public int B { get; }

      public UncompressedPaletteColor(int source, int position, short color) {
         (Source, Position, Color) = (source, position, color);
         (R, G, B) = ToRGB(color);
      }

      public static string Convert(short color) {
         var (r, g, b) = ToRGB(color);
         return $"{r}:{g}:{b}";
      }

      public static (int r, int g, int b) ToRGB(short color) {
         int r = (color >> 10) & 0x1F;
         int g = (color >> 5) & 0x1F;
         int b = (color >> 0) & 0x1F;
         return (r, g, b);
      }

      public static short Pack(int red, int green, int blue) {
         return (short)((red << 10) | (green << 5) | blue);
      }

      public bool Equals(IDataFormat other) {
         if (!(other is UncompressedPaletteColor that)) return false;
         return Source == that.Source && Position == that.Position && Color == that.Color;
      }

      public void Visit(IDataFormatVisitor visitor, byte data) => visitor.Visit(this, data);

      public override string ToString() => Convert(Color);
   }
}
