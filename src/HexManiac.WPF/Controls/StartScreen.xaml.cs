﻿using System.Diagnostics;
using System.Windows.Input;

namespace HavenSoft.HexManiac.WPF.Controls {
   public partial class StartScreen {
      public StartScreen() => InitializeComponent();

      private void PointerHelp(object sender, MouseButtonEventArgs e) {
         Process.Start(new ProcessStartInfo("https://github.com/haven1433/HexManiacAdvance/wiki/Pointers-and-Anchors"));
         e.Handled = true;
      }
   }
}
