﻿using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using MahApps.Metro;
using Wabbajack.Common;

namespace Wabbajack
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            CLI.ParseOptions(Environment.GetCommandLineArgs());
            if(CLIArguments.Help)
                CLI.DisplayHelpText();
        }
    }
}
