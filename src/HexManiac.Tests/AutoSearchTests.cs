﻿
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.QuickEditItems;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// The Auto-Search tests exist to verify that data can be found in the Vanilla roms.
   /// These tests are skippable, so that they'll work even if you don't have the ROMs on your system.
   /// This is important, since the ROMs aren't part of the repository.
   /// </summary>
   public class AutoSearchTests : IClassFixture<AutoSearchFixture> {

      public static IEnumerable<object[]> PokemonGames { get; } = new[] {
         "Ruby",
         "Sapphire",
         "FireRed",
         "LeafGreen",
         "Emerald",
         "DarkRisingKAIZO", // from FireRed
         "Vega 2019-04-20", // from FireRed
         "Clover",          // from FireRed
         "Gaia v3.2",       // from FireRed
         "Altair",          // from Emerald
      }.Select(game => new object[] { "sampleFiles/Pokemon " + game + ".gba" });

      private readonly AutoSearchFixture fixture;

      public AutoSearchTests(AutoSearchFixture fixture) => this.fixture = fixture;

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void PokemonNamesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, EggMoveRun.PokemonNameTable);
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Gaia")) Assert.Equal(1111, run.ElementCount);
         else Assert.Equal(412, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void MovesNamesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, EggMoveRun.MoveNamesTable);
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Vega")) Assert.Equal(512, run.ElementCount);
         else if (game.Contains("Clover")) Assert.Equal(512, run.ElementCount);
         else if (game.Contains("Gaia")) Assert.Equal(512, run.ElementCount);
         else Assert.Equal(355, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void MoveDescriptionsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var moveNamesAddress = model.GetAddressFromAnchor(noChange, -1, EggMoveRun.MoveNamesTable);
         var moveNamesRun = (ArrayRun)model.GetNextAnchor(moveNamesAddress);

         var moveDescriptionsAddress = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.MoveDescriptionsName);
         var moveDescriptionsRun = (ArrayRun)model.GetNextAnchor(moveDescriptionsAddress);

         Assert.Equal(moveNamesRun.ElementCount - 1, moveDescriptionsRun.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void AbilitiyNamesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "abilitynames");
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Clover")) Assert.Equal(156, run.ElementCount);
         else if (game.Contains("Gaia")) Assert.Equal(188, run.ElementCount);
         else Assert.Equal(78, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void AbilitiyDescriptionsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "abilitydescriptions");
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Clover")) Assert.Equal(156, run.ElementCount);
         else if (game.Contains("Gaia")) Assert.Equal(188, run.ElementCount);
         else Assert.Equal(78, run.ElementCount);

         if (game.Contains("Gaia")) return; // don't validate description text in Gaia, it's actually invalid.

         for (var i = 0; i < run.ElementCount; i++) {
            address = model.ReadPointer(run.Start + i * 4);
            var childRun = model.GetNextRun(address);
            Assert.IsType<PCSRun>(childRun);
         }
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TypesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "types");
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Clover")) Assert.Equal(24, run.ElementCount);
         else if (game.Contains("Gaia")) Assert.Equal(25, run.ElementCount);
         else Assert.Equal(18, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void ItemsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "items");
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Altair")) Assert.Equal(377, run.ElementCount);
         else if (game.Contains("Emerald")) Assert.Equal(377, run.ElementCount);
         else if (game.Contains("FireRed")) Assert.Equal(375, run.ElementCount);
         else if (game.Contains("DarkRisingKAIZO")) Assert.Equal(375, run.ElementCount);
         else if (game.Contains("LeafGreen")) Assert.Equal(375, run.ElementCount);
         else if (game.Contains("Ruby")) Assert.Equal(349, run.ElementCount);
         else if (game.Contains("Sapphire")) Assert.Equal(349, run.ElementCount);
         else if (game.Contains("Vega")) Assert.Equal(375, run.ElementCount);
         else if (game.Contains("Clover")) Assert.Equal(375, run.ElementCount);
         else if (game.Contains("Gaia")) Assert.Equal(375, run.ElementCount);
         else throw new NotImplementedException();
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void ItemImagesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "itemimages");
         if (game.Contains("Ruby") || game.Contains("Sapphire")) {
            Assert.Equal(Pointer.NULL, address);
            return;
         }

         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Altair") || game.Contains("Emerald")) Assert.Equal(377, run.ElementCount);
         else Assert.Equal(375, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void DecorationsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.DecorationsTableName);
         var run = (ArrayRun)model.GetNextAnchor(address);
         Assert.Equal(121, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TrainerClassNamesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "trainerclassnames");
         var run = (ArrayRun)model.GetNextAnchor(address);
         if (game.Contains("Altair")) Assert.Equal(66, run.ElementCount);
         else if (game.Contains("Emerald")) Assert.Equal(66, run.ElementCount);
         else if (game.Contains("FireRed")) Assert.Equal(107, run.ElementCount);
         else if (game.Contains("DarkRisingKAIZO")) Assert.Equal(107, run.ElementCount);
         else if (game.Contains("LeafGreen")) Assert.Equal(107, run.ElementCount);
         else if (game.Contains("Ruby")) Assert.Equal(58, run.ElementCount);
         else if (game.Contains("Sapphire")) Assert.Equal(58, run.ElementCount);
         else if (game.Contains("Vega")) Assert.Equal(107, run.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void PokeStatsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "pokestats");
         var run = (ArrayRun)model.GetNextAnchor(address);

         var firstPokemonStats = model.Skip(run.Start + run.ElementLength).Take(6).ToArray();
         var compareSet = new[] { 45, 49, 49, 45, 65, 65 }; // Bulbasaur
         if (game.Contains("Vega")) compareSet = new[] { 42, 53, 40, 70, 63, 40 }; // Nimbleaf
         if (game.Contains("Clover")) compareSet = new[] { 56, 60, 55, 50, 47, 50 }; // Grasshole
         for (int i = 0; i < compareSet.Length; i++) Assert.Equal(compareSet[i], firstPokemonStats[i]);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void LvlUpMovesAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.LevelMovesTableName);
         var run = (ArrayRun)model.GetNextAnchor(address);
         Assert.NotNull(run);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void EvolutionsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.EvolutionTableName);
         var run = model.GetNextAnchor(address);
         Assert.IsType<ArrayRun>(run);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void PokedexDataIsFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var regional = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.RegionalDexTableName);
         var national = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.NationalDexTableName);
         var conversion = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.ConversionDexTableName);
         var info = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.DexInfoTableName);

         var regionalRun = model.GetNextAnchor(regional);
         var nationalRun = model.GetNextAnchor(national);
         var conversionRun = model.GetNextAnchor(conversion);
         var infoRun = (ArrayRun)model.GetNextAnchor(info);

         Assert.IsType<ArrayRun>(regionalRun);
         Assert.IsType<ArrayRun>(nationalRun);
         Assert.IsType<ArrayRun>(conversionRun);

         if (game.Contains("Clover") || game.Contains("Gaia")) return; // some hacks have busted dex data

         Assert.Equal(387, infoRun.ElementCount);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void MoveDataFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, "movedata");
         var run = (ArrayRun)model.GetNextAnchor(address);

         var poundStats = model.Skip(run.Start + run.ElementLength).Take(8).ToArray();
         var compareSet = new[] { 0, 40, 0, 100, 35, 0, 0, 0 };
         for (int i = 0; i < compareSet.Length; i++) Assert.Equal(compareSet[i], poundStats[i]);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void EggMoveDataFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var address = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.EggMovesTableName);
         var run = (EggMoveRun)model.GetNextAnchor(address);

         if (game.Contains("Vega")) Assert.Equal(3, run.PointerSources.Count); // there's a false positive in Vega... for now! Would be nice if this were 2, but it doesn't much matter.
         else Assert.Equal(2, run.PointerSources.Count);
         var expectedLastElement = model.ReadMultiByteValue(run.PointerSources[1] - 4, 4);
         var expectedLength = expectedLastElement + 1;
         var actualLength = run.Length / 2 - 1;  // remove the closing element.
         Assert.InRange(actualLength, 790, expectedLength);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TutorsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var movesLocation = model.GetAddressFromAnchor(noChange, -1, AutoSearchModel.MoveTutors);
         var compatibilityLocation = model.GetAddressFromAnchor(noChange, -1, AutoSearchModel.TutorCompatibility);

         // ruby and sapphire have no tutors
         if (game.Contains("Ruby") || game.Contains("Sapphire")) {
            Assert.Equal(Pointer.NULL, movesLocation);
            Assert.Equal(Pointer.NULL, compatibilityLocation);
            return;
         }

         var moves = (ArrayRun)model.GetNextRun(movesLocation);
         var compatibility = (ArrayRun)model.GetNextRun(compatibilityLocation);

         var expectedMoves = game.Contains("Emerald") || game.Contains("Altair") ? 30 : 15;
         var compatibilityElementLength = (int)Math.Ceiling(expectedMoves / 8.0);

         Assert.Equal(expectedMoves, moves.ElementCount);
         Assert.Equal(compatibilityElementLength, compatibility.ElementContent[0].Length);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void WildPokemonAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var wildLocation = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.WildTableName);
         var wild = (ArrayRun)model.GetNextRun(wildLocation);

         // TODO
      }

      [SkippableFact]
      public void TutorsCompatibilityContainsCorrectDataFireRed() {
         var model = fixture.LoadModel(PokemonGames.Select(array => (string)array[0]).Single(game => game.Contains("FireRed")));
         var address = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, AutoSearchModel.TutorCompatibility);
         Assert.Equal(0x409A, model.ReadMultiByteValue(address + 2, 2));
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TmsAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var movesLocation = model.GetAddressFromAnchor(noChange, -1, AutoSearchModel.TmMoves);
         var hmLocation = model.GetAddressFromAnchor(noChange, -1, AutoSearchModel.HmMoves);
         var compatibilityLocation = model.GetAddressFromAnchor(noChange, -1, AutoSearchModel.TmCompatibility);

         var tmMoves = (ArrayRun)model.GetNextRun(movesLocation);
         var hmMoves = (ArrayRun)model.GetNextRun(hmLocation);
         var compatibility = (ArrayRun)model.GetNextRun(compatibilityLocation);

         var expectedTmMoves = 58;
         var expectedHmMoves = 8;
         var compatibilityElementLength = 8;

         Assert.Equal(expectedTmMoves, tmMoves.ElementCount);
         Assert.Equal(expectedHmMoves, hmMoves.ElementCount);
         Assert.Equal(compatibilityElementLength, compatibility.ElementContent[0].Length);
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void MultichoiceAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var multichoiceAddress = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.MultichoiceTableName);
         var multichoice = (ArrayRun)model.GetNextRun(multichoiceAddress);
         if (game.Contains("DarkRising")) return;            // dark rising has bugged pointers in the 2nd one, so we don't expect to find many multichoice.
         Assert.NotInRange(multichoice.ElementCount, 0, 30); // make sure we found at least a few
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TypeChartIsFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         var typeChartAddress = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.TypeChartTableName);
         var typeChart = (ITableRun)model.GetNextRun(typeChartAddress);
         Assert.NotInRange(typeChart.ElementCount, 0, 100);

         var typeChartAddress2 = model.GetAddressFromAnchor(noChange, -1, HardcodeTablesModel.TypeChartTableName2);
         var typeChart2 = (ITableRun)model.GetNextRun(typeChartAddress2);
      }

      // this one actually changes the data, so I can't use the same shared model as everone else.
      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void ExpandableTutorsWorks(string game) {
         var fileSystem = new StubFileSystem();
         var model = fixture.LoadModelNoCache(game);
         var editor = new EditorViewModel(fileSystem, false);
         var viewPort = new ViewPort(game, model);
         editor.Add(viewPort);
         var expandTutors = editor.QuickEdits.Single(edit => edit.Name == "Make Tutors Expandable");

         // ruby/sapphire do not support this quick-edit
         var canRun = expandTutors.CanRun(viewPort);
         if (game.Contains("Ruby") || game.Contains("Sapphire")) {
            Assert.False(canRun);
            return;
         } else {
            Assert.True(canRun);
         }

         // run the actual quick-edit
         expandTutors.Run(viewPort);

         // extend the table
         var table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.MoveTutors));
         viewPort.Goto.Execute((table.Start + table.Length).ToString("X6"));
         viewPort.Edit("+");

         // the 4 bytes after the last pointer to tutor-compatibility should store the length of tutormoves
         table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.MoveTutors));
         var tutorCompatibilityPointerSources = model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.TutorCompatibility)).PointerSources;
         var word = (WordRun)model.GetNextRun(tutorCompatibilityPointerSources.First() + 4);
         Assert.Equal(table.ElementCount, model.ReadValue(word.Start));
      }

      [SkippableTheory]
      [MemberData(nameof(PokemonGames))]
      public void TrainersAreFound(string game) {
         var model = fixture.LoadModel(game);
         var noChange = new NoDataChangeDeltaModel();

         Assert.True(model.TryGetNameArray(HardcodeTablesModel.TrainerTableName, out var trainers));
         if (game.Contains("Emerald")) Assert.Equal(855, trainers.ElementCount);
         else if (game.Contains("Altair")) Assert.Equal(1, trainers.ElementCount); // actually has 855, but the first element is glitched in a way that I shouldn't auto-recover.
         else if (game.Contains("FireRed")) Assert.Equal(743, trainers.ElementCount);
         else if (game.Contains("LeafGreen")) Assert.Equal(743, trainers.ElementCount);
         else if (game.Contains("Clover")) Assert.Equal(743, trainers.ElementCount);
         else if (game.Contains("Vega")) Assert.Equal(743, trainers.ElementCount);
         else if (game.Contains("DarkRisingKAIZO")) Assert.Equal(742, trainers.ElementCount); // the last one is glitched
         else if (game.Contains("Gaia")) Assert.Equal(743, trainers.ElementCount);
         else if (game.Contains("Ruby")) Assert.Equal(694, trainers.ElementCount);
         else if (game.Contains("Sapphire")) Assert.Equal(694, trainers.ElementCount);
         else throw new NotImplementedException();
      }

      // this one actually changes the data, so I can't use the same shared model as everone else.
      // [SkippableTheory] // test removed until feature is complete.
      // [MemberData(nameof(PokemonGames))]
      private void ExpandableTMsWorks(string game) {
         var fileSystem = new StubFileSystem();
         var model = fixture.LoadModelNoCache(game);
         var editor = new EditorViewModel(fileSystem, false);
         var viewPort = new ViewPort(game, model);
         editor.Add(viewPort);
         var expandTMs = editor.QuickEdits.Single(edit => edit.Name == "Make TMs Expandable");

         // Clover makes changes that prevent us from finding tmmoves/tmcompatibility. Don't support Clover.
         var canRun = expandTMs.CanRun(viewPort);
         if (game.Contains("Clover")) {
            Assert.False(canRun);
            return;
         } else {
            Assert.True(canRun);
         }

         // run the actual quick-edit
         expandTMs.Run(viewPort);

         // extend the table
         var table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.TmMoves));
         viewPort.Goto.Execute((table.Start + table.Length).ToString("X6"));
         viewPort.Edit("+");

         // the 4 bytes after the last pointer to tm-compatibility should store the length of tmmoves
         table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.TmMoves));
         var tmCompatibilityPointerSources = model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, AutoSearchModel.TmCompatibility)).PointerSources;
         var word = (WordRun)model.GetNextRun(tmCompatibilityPointerSources.First() + 4);
         Assert.Equal(table.ElementCount, model.ReadValue(word.Start));
      }

      // this one actually changes the data, so I can't use the same shared model as everone else.
      // [SkippableTheory] // test removed until feature is complete.
      // [MemberData(nameof(PokemonGames))]
      private void ExpandableItemsWorks(string game) {
         var fileSystem = new StubFileSystem();
         var model = fixture.LoadModelNoCache(game);
         var editor = new EditorViewModel(fileSystem, false);
         var viewPort = new ViewPort(game, model);
         editor.Add(viewPort);
         var expandTMs = editor.QuickEdits.Single(edit => edit.Name == "Make Items Expandable");

         // run the actual quick-edit
         expandTMs.Run(viewPort);

         // extend the table
         var table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, "items"));
         viewPort.Goto.Execute((table.Start + table.Length).ToString("X6"));
         viewPort.Edit("+");

         // 0x14 bytes after the start of the change should store the length of items
         var gameCode = new string(Enumerable.Range(0xAC, 4).Select(i => ((char)model[i])).ToArray());
         var editStart = MakeItemsExpandable.GetPrimaryEditAddress(gameCode);
         table = (ArrayRun)model.GetNextRun(model.GetAddressFromAnchor(new ModelDelta(), -1, "items")); // note that since we changed the table, we have to get the run again.
         var word = (WordRun)model.GetNextRun(editStart + 0x14);
         Assert.Equal(table.ElementCount, model.ReadValue(word.Start));
      }
   }

   /// <summary>
   /// Loading the model can take a while.
   /// We want to know that loading the model created the correct arrays,
   /// But loading the same file into a model multiple times just wastes time.
   /// Go ahead and cache a model loaded from a file the first time,
   /// so each individual test doesn't have to do it again.
   ///
   /// This is done as a Fixture instead of a Lazy because all the tests in question
   /// are part of the same Test Collection (because they're in the same class)
   /// </summary>
   public class AutoSearchFixture {
      private IDictionary<string, PokemonModel> modelCache = new Dictionary<string, PokemonModel>();

      public AutoSearchFixture() {
         Parallel.ForEach(AutoSearchTests.PokemonGames.Select(array => (string)array[0]), name => {
            if (!File.Exists(name)) return;
            var data = File.ReadAllBytes(name);
            var metadata = new StoredMetadata(new string[0]);
            var model = new HardcodeTablesModel(data, metadata);
            lock (modelCache) modelCache[name] = model;
         });
      }

      public PokemonModel LoadModel(string name) {
         Skip.IfNot(modelCache.ContainsKey(name));
         return modelCache[name];
      }

      /// <summary>
      /// Make a copy of one of the existing models, but quickly, instead of doing a full load from file.
      /// </summary>
      /// <param name="name"></param>
      /// <returns></returns>
      public IDataModel LoadModelNoCache(string name) {
         Skip.IfNot(File.Exists(name));
         var template = modelCache[name];
         var metadata = template.ExportMetadata();
         var model = new PokemonModel(template.RawData.ToArray(), metadata);
         return model;
      }
   }
}
