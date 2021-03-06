﻿using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   public interface IFormattedRun {
      int Start { get; }
      int Length { get; }
      SortedSpan<int> PointerSources { get; }
      string FormatString { get; }
      IDataFormat CreateDataFormat(IDataModel data, int index);
      IFormattedRun MergeAnchor(SortedSpan<int> sources);
      IFormattedRun RemoveSource(int source);
      IFormattedRun Duplicate(int start, SortedSpan<int> pointerSources);
   }

   public interface IAppendToBuilderRun : IFormattedRun {
      void AppendTo(IDataModel model, StringBuilder builder, int start, int length, bool deep);
   }

   /// <summary>
   /// A run representing a stream.
   /// Streams usually are variable length, and end with some sort of 'end' token.
   /// We want to be able to stringify streams, so we can use them with the tools.
   /// </summary>
   public interface IStreamRun : IFormattedRun {
      /// <summary>
      /// Should not change the data, only creates a string representation of that data.
      /// </summary>
      string SerializeRun();

      /// <summary>
      /// Updates data based on converting content back into the stream.
      /// Returns the run where the data was placed.
      /// The run usually starts at the same spot as before, but in the case of repointing it can be different.
      /// </summary>
      IStreamRun DeserializeRun(string content, ModelDelta token);

      bool DependsOn(string anchorName);
   }

   public class FormattedRunComparer : IComparer<IFormattedRun> {
      public static FormattedRunComparer Instance { get; } = new FormattedRunComparer();
      public int Compare(IFormattedRun a, IFormattedRun b) => a.Start.CompareTo(b.Start);
   }

   /// <summary>
   /// Converts from a start index to an IFormattedRun, for comparison purposes.
   /// </summary>
   public class CompareFormattedRun : IFormattedRun {
      public int Start { get; }
      public int Length => 0;
      public string FormatString => throw new NotImplementedException();

      public CompareFormattedRun(int start) => Start = start;

      public SortedSpan<int> PointerSources => throw new NotImplementedException();
      public IDataFormat CreateDataFormat(IDataModel data, int index) => throw new NotImplementedException();
      public IFormattedRun MergeAnchor(SortedSpan<int> other) => throw new NotImplementedException();
      public IFormattedRun RemoveSource(int source) => throw new NotImplementedException();

      public IFormattedRun Duplicate(int start, SortedSpan<int> pointerSources) => throw new NotImplementedException();
   }

   public abstract class BaseRun : IFormattedRun {
      public const char AnchorStart = '^';

      public int Start { get; private set; }
      public abstract int Length { get; }
      public abstract string FormatString { get; }
      public SortedSpan<int> PointerSources { get; private set; }

      public BaseRun(int start, SortedSpan<int> sources = null) {
         Start = start;
         PointerSources = sources;
      }

      public abstract IDataFormat CreateDataFormat(IDataModel data, int index);

      public IFormattedRun MergeAnchor(SortedSpan<int> sources) {
         if (sources == null) return this;

         if (PointerSources == null) return Clone(sources);

         return Clone(PointerSources.Add(sources));

         //var set = new SortedSet<int>(PointerSources);
         //if (sources != null) foreach (var source in sources) set.Add(source);
         //return Clone(set.ToList());
         // return Clone(sources.Concat(PointerSources).Distinct().OrderBy(i => i).ToList());
      }

      public virtual IFormattedRun RemoveSource(int source) {
         //var sources = new List<int>(PointerSources);
         //sources.Remove(source);
         //return Clone(sources);
         // return Clone(PointerSources.Except(new[] { source }).ToList());
         return Clone(PointerSources.Remove1(source));
      }

      public IFormattedRun Duplicate(int start, SortedSpan<int> pointerSources) {
         var myStart = Start;
         Start = start;
         var copy = Clone(pointerSources);
         Start = myStart;
         return copy;
      }

      protected abstract BaseRun Clone(SortedSpan<int> newPointerSources);
   }

   public class NoInfoRun : BaseRun {
      public static NoInfoRun NullRun { get; } = new NoInfoRun(int.MaxValue);  // effectively a null object

      public override int Length => 1;
      public override string FormatString => string.Empty;

      public NoInfoRun(int start, SortedSpan<int> sources = null) : base(start, sources) { }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) => None.Instance;
      protected override BaseRun Clone(SortedSpan<int> newPointerSources) {
         return new NoInfoRun(Start, newPointerSources);
      }
   }

   public class SortedSpan<T> : IReadOnlyList<T> where T : IComparable<T> {
      private readonly T[] elements;
      public int Count { get; }
      public T this[int index] => elements[index];
      public static SortedSpan<T> None { get; } = new SortedSpan<T>(new T[0], 0);

      public SortedSpan(T single) => (elements, Count) = (new[] { single }, 1);

      public SortedSpan(IReadOnlyList<T> source) {
         var set = new SortedSet<T>(source);
         Count = set.Count;
         elements = new T[set.Count];
         int i = 0;
         foreach (var t in set) elements[i++] = t;
      }

      private SortedSpan(T[] elements, int length) => (this.elements, Count) = (elements, length);

      public SortedSpan<T> Add1(T value) {
         var newElements = new T[Count + 1];
         int i = 0, j = 0, compare = 0;
         while (j < Count) {
            compare = elements[j].CompareTo(value);
            if (compare < 0) newElements[i++] = elements[j++];
            else break;
         }
         if (j < Count && compare == 0) j++;
         newElements[i++] = value;
         Array.Copy(elements, j, newElements, i, Count - j);
         return new SortedSpan<T>(newElements, newElements.Length);
      }

      public SortedSpan<T> Remove1(T value) {
         var newElements = new T[Count];
         int i = 0, compare = 0;
         while (i < Count) {
            compare = elements[i].CompareTo(value);
            if (compare == 0) break;
            newElements[i] = elements[i++];
         }
         if (Count - 1 - i > 0) { Array.Copy(elements, i + 1, newElements, i, Count - 1 - i); i = Count - 1; }
         return new SortedSpan<T>(newElements, i);
      }

      public SortedSpan<T> Add(SortedSpan<T> other) {
         var newElements = new T[Count + other.Count];
         int a = 0, b = 0, d = 0, compare = 0;
         while (a < Count && b < other.Count) {
            compare = elements[a].CompareTo(other.elements[b]);
            if (compare < 0) {
               newElements[d++] = elements[a++];
            } else {
               newElements[d++] = other.elements[b++];
               if (compare == 0) a++;
            }
         }
         if (a < Count) { Array.Copy(elements, a, newElements, d, Count - a); d += Count - a; }
         if (b < other.Count) { Array.Copy(other.elements, b, newElements, d, other.Count - b); d += other.Count - b; }
         return new SortedSpan<T>(newElements, d);
      }

      public SortedSpan<T> Remove(SortedSpan<T> other) {
         var newElements = new T[Count];
         int a = 0, b = 0, d = 0, compare = 0;
         while (a < Count && b < other.Count) {
            compare = elements[a].CompareTo(other.elements[b]);
            if (compare > 0) {
               b++;
            } else if (compare < 0) {
               newElements[d++] = elements[a++];
            } else {
               a++; b++;
            }
         }
         Array.Copy(elements, a, newElements, d, Count - a);
         return new SortedSpan<T>(newElements, d);
      }

      public IEnumerator<T> GetEnumerator() => elements.Take(Count).GetEnumerator();
      IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
   }
}
