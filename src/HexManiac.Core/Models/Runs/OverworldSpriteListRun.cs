﻿using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   public class OverworldSpriteListRun : BaseRun, ITableRun, ISpriteRun {
      private readonly IDataModel model;
      private readonly IReadOnlyList<ArrayRunElementSegment> parent;
      public static readonly string SharedFormatString = AsciiRun.StreamDelimeter + "osl" + AsciiRun.StreamDelimeter;

      public override int Length { get; }

      public int ElementCount { get; }

      public override string FormatString => SharedFormatString;

      public int ElementLength => 8;

      public IReadOnlyList<string> ElementNames { get; }

      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }

      public SpriteFormat SpriteFormat { get; }

      public int Pages => 1;

      public OverworldSpriteListRun(IDataModel model, IReadOnlyList<ArrayRunElementSegment> parent, int start, SortedSpan<int> sources = null) : base(start, sources) {
         this.model = model;
         this.parent = parent;
         var segments = new List<ArrayRunElementSegment> {
            new ArrayRunPointerSegment("sprite", "`ucs4x1x2`"),
            new ArrayRunElementSegment("length", ElementContentType.Integer, 4),
         };
         ElementContent = segments;
         ElementCount = 1;
         Length = ElementLength;
         SpriteFormat = new SpriteFormat(4, 1, 1, string.Empty);

         if (sources == null || sources.Count == 0) return;

         // initialize format from parent info
         var listOffset = GetOffset<ArrayRunPointerSegment>(parent, pSeg => pSeg.InnerFormat == SharedFormatString);
         var widthOffset = GetOffset(parent, seg => seg.Name == "width");
         var heightOffset = GetOffset(parent, seg => seg.Name == "height");
         var keyOffset = GetOffset(parent, seg => seg.Name == "paletteid");

         var elementStart = sources[0] - listOffset;
         var width = Math.Max(1, model.ReadMultiByteValue(elementStart + widthOffset, 2));
         var height = Math.Max(1, model.ReadMultiByteValue(elementStart + heightOffset, 2));
         var tileWidth = (int)Math.Max(1, Math.Ceiling(width / 8.0));
         var tileHeight = (int)Math.Max(1, Math.Ceiling(height / 8.0));
         var key = model.ReadMultiByteValue(elementStart + keyOffset, 2);
         var hint = $"overworld.palettes:id={key:X4}";

         var format = $"`ucs4x{width / 8}x{height / 8}|{hint}`";
         segments[0] = new ArrayRunPointerSegment("sprite", format);

         // calculate the element count
         var byteLength = tileWidth * tileHeight * TileSize;
         var nextAnchorStart = model.GetNextAnchor(Start + 1).Start;
         ElementCount = 0;
         Length = 0;
         while (Start + Length < nextAnchorStart) {
            if (model[start + Length + 3] != 0x08) break;
            if (model.ReadMultiByteValue(start + Length + 4, 4) != byteLength) break;
            ElementCount += 1;
            Length += ElementLength;
         }

         SpriteFormat = new SpriteFormat(4, tileWidth * ElementCount, tileHeight, hint);
         ElementNames = Enumerable.Range(0, ElementCount).Select(i => string.Empty).ToList();
      }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) => ITableRunExtensions.CreateSegmentDataFormat(this, data, index);

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) => new OverworldSpriteListRun(model, parent, Start, newPointerSources);

      public ITableRun Append(ModelDelta token, int length) => throw new NotImplementedException();

      public void AppendTo(IDataModel model, StringBuilder builder, int start, int length, bool deep) => ITableRunExtensions.AppendTo(this, model, builder, start, length, deep);

      public OverworldSpriteListRun UpdateFromParent(ModelDelta token, int segmentIndex, int pointerSource, out bool spritesMoved) {
         spritesMoved = false;
         if (!(model.GetNextRun(pointerSource) is ITableRun tableSource)) return this;
         var segName = tableSource.ElementContent[segmentIndex].Name;
         if (!segName.IsAny("width", "height", "paletteid")) return this;

         // if parent width changed, add/subtract space to the right edge of the sprite. Min width is one tile.
         // if the parent height changed, add/subtract space to the bottom of the sprite. Min height is one tile.
         if (segName == "width" || segName == "height") {
            var listOffset = GetOffset<ArrayRunPointerSegment>(parent, pSeg => pSeg.InnerFormat == SharedFormatString);
            var elementStart = PointerSources[0] - listOffset;
            var widthOffset = GetOffset(parent, seg => seg.Name == "width");
            var heightOffset = GetOffset(parent, seg => seg.Name == "height");
            var width = Math.Max(1, model.ReadMultiByteValue(elementStart + widthOffset, 2));
            var height = Math.Max(1, model.ReadMultiByteValue(elementStart + heightOffset, 2));
            var newTileWidth = (int)Math.Max(1, Math.Ceiling(width / 8.0));
            var newTileHeight = (int)Math.Max(1, Math.Ceiling(height / 8.0));
            var movedRuns = new List<ISpriteRun>();
            for (int i = 0; i < ElementCount; i++) {
               var spriteStart = model.ReadPointer(Start + ElementLength * i);
               var sprite = model.GetNextRun(spriteStart) as ISpriteRun;
               if (!movedRuns.Contains(sprite) && sprite != null) {
                  sprite = Resize(token, sprite, newTileWidth, newTileHeight);
                  movedRuns.Add(sprite);
               }
               var spriteLengthStart = Start + ElementLength * i + 4;
               model.WriteMultiByteValue(spriteLengthStart, 4, token, newTileWidth * newTileHeight * TileSize);
               spritesMoved |= spriteStart != sprite.Start;
            }
         }

         // if the parent paletteid changed, we just need to update the format, no data change is required.
         return new OverworldSpriteListRun(model, parent, Start, PointerSources);
      }

      private static int GetOffset<T>(IReadOnlyList<ArrayRunElementSegment> segments, Func<T, bool> segmentIdentifier) where T : ArrayRunElementSegment
         => segments.Until(seg => seg is T t && segmentIdentifier(t)).Sum(seg => seg.Length);

      private static int GetOffset(IReadOnlyList<ArrayRunElementSegment> segments, Func<ArrayRunElementSegment, bool> segmentIdentifier)
         => segments.Until(segmentIdentifier).Sum(seg => seg.Length);

      const int TileSize = 32;
      private ISpriteRun Resize(ModelDelta token, ISpriteRun spriteRun, int tileWidth, int tileHeight) {
         if (spriteRun == null) return spriteRun;
         spriteRun = (ISpriteRun)model.RelocateForExpansion(token, spriteRun, tileWidth * tileHeight * TileSize);
         var format = spriteRun.SpriteFormat;

         // extract existing tile data
         var existingTiles = new byte[format.TileWidth, format.TileHeight][];
         for (int x = 0; x < format.TileWidth; x++) {
            for (int y = 0; y < format.TileHeight; y++) {
               var tileIndex = y * format.TileWidth + x;
               existingTiles[x, y] = new byte[TileSize];
               Array.Copy(model.RawData, spriteRun.Start + tileIndex * TileSize, existingTiles[x, y], 0, TileSize);
            }
         }

         // rewrite it with the new dimensions
         for (int x = 0; x < tileWidth; x++) {
            for (int y = 0; y < tileHeight; y++) {
               var tileIndex = y * tileWidth + x;
               var start = spriteRun.Start + tileIndex * TileSize;
               for (int i = 0; i < TileSize; i++) {
                  if (x < format.TileWidth && y < format.TileHeight) {
                     token.ChangeData(model, start + i, existingTiles[x, y][i]);
                  } else {
                     token.ChangeData(model, start + i, 0);
                  }
               }
            }
         }

         return spriteRun;
      }

      public int[,] GetPixels(IDataModel model, int page) {
         var listOffset = GetOffset<ArrayRunPointerSegment>(parent, pSeg => pSeg.InnerFormat == SharedFormatString);
         var elementStart = PointerSources[0] - listOffset;
         var widthOffset = GetOffset(parent, seg => seg.Name == "width");
         var heightOffset = GetOffset(parent, seg => seg.Name == "height");
         var width = Math.Max(1, model.ReadMultiByteValue(elementStart + widthOffset, 2));
         var height = Math.Max(1, model.ReadMultiByteValue(elementStart + heightOffset, 2));

         var overallPixels = new int[width * ElementCount, height];

         for (int i = 0; i < ElementCount; i++) {
            var spriteStart = model.ReadPointer(Start + ElementLength * i);
            if (!(model.GetNextRun(spriteStart) is ISpriteRun spriteRun)) continue;
            var spritePixels = spriteRun.GetPixels(model, 0);
            if (spritePixels.GetLength(0) < width || spritePixels.GetLength(1) < height) continue;
            int offset = width * i;
            for (int x = 0; x < width; x++) {
               for (int y = 0; y < height; y++) {
                  overallPixels[offset + x, y] = spritePixels[x, y];
               }
            }
         }

         return overallPixels;
      }

      public ISpriteRun SetPixels(IDataModel model, ModelDelta token, int page, int[,] pixels) {
         throw new NotImplementedException();
      }

      public ISpriteRun Duplicate(SpriteFormat newFormat) {
         throw new NotImplementedException();
      }
   }
}
