﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class ViewPortAutocompleteEditTests {
      private readonly List<string> errors = new List<string>();
      private readonly ViewPort viewPort;

      public ViewPortAutocompleteEditTests() {
         var model = new PokemonModel(new byte[0x200]);
         viewPort = new ViewPort("name.txt", model) { Height = 0x10, Width = 0x10 };
         viewPort.OnError += (sender, e) => errors.Add(e);

         viewPort.SelectionStart = new Point(0, 8);
         viewPort.Edit("^label ");

         viewPort.SelectionStart = new Point(4, 8);
         viewPort.Edit("^labwork ");

         viewPort.SelectionStart = new Point(8, 8);
         viewPort.Edit("^othertext ");

         viewPort.SelectionStart = new Point(12, 8);
         viewPort.Edit("^sometext[name\"\"6]6 \"crazy\" \"crozy\" \"short\" \"shoot\" ");
         viewPort.Goto.Execute("0"); // after making a table, reset the view

         viewPort.SelectionStart = new Point(0, 12);
         viewPort.Edit("^table[num:sometext]6 ");
         viewPort.Goto.Execute("0"); // after making a table, reset the view

         viewPort.SelectionStart = new Point();
      }

      [Fact]
      public void UnderEditLoosePointerGetsAutoComplete() {
         viewPort.Edit("<labe");

         var format = (UnderEdit)viewPort[0, 0].Format;
         Assert.Single(format.AutocompleteOptions);

         format = (UnderEdit)viewPort[1, 0].Format;
         Assert.Null(format.AutocompleteOptions);
      }

      [Fact]
      public void BackspaceWidensAutocompleteResults() {
         viewPort.Edit("<labe");
         viewPort.Edit(ConsoleKey.Backspace);

         var format = (UnderEdit)viewPort[0, 0].Format;
         Assert.Equal(2, format.AutocompleteOptions.Count);
      }

      [Fact]
      public void UpDownDuringAutoCompleteSelectsResults() {
         viewPort.Edit("<lab");
         viewPort.MoveSelectionStart.Execute(Direction.Down);

         var format = (UnderEdit)viewPort[0, 0].Format;
         Assert.True(format.AutocompleteOptions[0].IsSelected);
      }

      [Fact]
      public void EmptyAutoCompleteArrowsActNormally() {
         viewPort.Edit("<xyz");
         viewPort.MoveSelectionStart.Execute(Direction.Down);

         Assert.IsNotType<UnderEdit>(viewPort[0, 0].Format);
      }

      [Fact]
      public void SelectAutoCompleteOptionAndHitEnterUsesThatResult() {
         viewPort.Edit("<lab");
         viewPort.MoveSelectionStart.Execute(Direction.Down);
         viewPort.Edit(ConsoleKey.Enter);

         var format = (Pointer)viewPort[0, 0].Format;
         Assert.Equal("label", format.DestinationName);
      }

      [Fact]
      public void AutocompleteWorksForEnums() {
         viewPort.SelectionStart = new Point(2, 12);
         viewPort.Edit("sho");

         var format = (UnderEdit)viewPort[2, 12].Format;
         Assert.Equal(2, format.AutocompleteOptions.Count);
      }

      [Fact]
      public void AutocompleteWorksForInlineGoto() {
         viewPort.Edit("@lab");

         var format = (UnderEdit)viewPort[0, 0].Format;
         Assert.Equal(2, format.AutocompleteOptions.Count);
      }

      [Fact]
      public void NoErrorWhenClickingOffOfAnEmptyPointerEdit() {
         viewPort.Edit("<");
         viewPort.SelectionStart = new Point(4, 4);

         Assert.Empty(errors);                                   // no errors
         Assert.NotEqual(0, viewPort.Model.GetNextRun(0).Start); // no run was added
      }
   }
}