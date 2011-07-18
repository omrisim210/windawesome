﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using IronPython.Hosting;
using IronRuby;
using Microsoft.Scripting.Hosting;

namespace Windawesome
{
	public class Config
	{
		public IPlugin[] Plugins { get; set; }
		public IBar[] Bars { get; set; }
		public ILayout[] Layouts { get; set; }
		public int WorkspacesCount { get; set; }
		public Workspace[] Workspaces { get; set; }
		public int StartingWorkspace { get; set; }
		public ProgramRule[] ProgramRules { get; set; }
		public int BorderWidth { get; set; }
		public int PaddedBorderWidth { get; set; }
		public Tuple<NativeMethods.MOD, System.Windows.Forms.Keys> UniqueHotkey { get; set; }

		internal Config()
		{
			this.StartingWorkspace = 1;
			this.BorderWidth = -1;
			this.PaddedBorderWidth = -1;
		}

		internal void LoadPlugins(Windawesome windawesome)
		{
			const string layoutsDirName = "Layouts";
			const string widgetsDirName = "Widgets";
			const string pluginsDirName = "Plugins";
			const string configDirName	= "Config";

			if (!Directory.Exists(configDirName) || Directory.EnumerateFiles(configDirName).FirstOrDefault() == null)
			{
				throw new Exception("You HAVE to have a " + configDirName + " directory in the folder and it must " +
					"contain at least one Python or Ruby file that initializes all instance variables in 'config' " +
					"that don't have default values!");
			}
			if (!Directory.Exists(layoutsDirName))
			{
				Directory.CreateDirectory(layoutsDirName);
			}
			if (!Directory.Exists(widgetsDirName))
			{
				Directory.CreateDirectory(widgetsDirName);
			}
			if (!Directory.Exists(pluginsDirName))
			{
				Directory.CreateDirectory(pluginsDirName);
			}
			var files =
				Directory.EnumerateFiles(layoutsDirName).Select(fileName => new FileInfo(fileName)) .Concat(
				Directory.EnumerateFiles(widgetsDirName).Select(fileName => new FileInfo(fileName))).Concat(
				Directory.EnumerateFiles(pluginsDirName).Select(fileName => new FileInfo(fileName))).Concat(
				Directory.EnumerateFiles(configDirName) .Select(fileName => new FileInfo(fileName)));

			PluginLoader.LoadAll(windawesome, this, files);
		}

		private static class PluginLoader
		{
			private static ScriptEngine pythonEngine;
			private static ScriptEngine rubyEngine;

			private static ScriptEngine PythonEngine
			{
				get
				{
					if (pythonEngine == null)
					{
						pythonEngine = Python.CreateEngine();
						InitializeScriptEngine(pythonEngine);
					}
					return pythonEngine;
				}
			}

			private static ScriptEngine RubyEngine
			{
				get
				{
					if (rubyEngine == null)
					{
						rubyEngine = Ruby.CreateEngine();
						InitializeScriptEngine(rubyEngine);
					}
					return rubyEngine;
				}
			}

			private static void InitializeScriptEngine(ScriptEngine engine)
			{
				var searchPaths = engine.GetSearchPaths().ToList();
				searchPaths.Add(Environment.CurrentDirectory);
				engine.SetSearchPaths(searchPaths);

				AppDomain.CurrentDomain.GetAssemblies().ForEach(engine.Runtime.LoadAssembly);
			}

			private static ScriptEngine GetEngineForFile(FileSystemInfo file)
			{
				switch (file.Extension)
				{
					case ".ir":
					case ".rb":
						return RubyEngine;
					case ".ipy":
					case ".py":
						return PythonEngine;
					case ".dll":
						var assembly = Assembly.LoadFrom(file.FullName);
						if (rubyEngine != null)
						{
							rubyEngine.Runtime.LoadAssembly(assembly);
						}
						if (pythonEngine != null)
						{
							pythonEngine.Runtime.LoadAssembly(assembly);
						}
						break;
				}

				return null;
			}

			internal static void LoadAll(Windawesome windawesome, Config config, IEnumerable<FileInfo> files)
			{
				ScriptScope scope = null;
				ScriptEngine previousLanguage = null;
				foreach (var file in files)
				{
					var engine = GetEngineForFile(file);
					if (engine != null)
					{
						if (scope == null)
						{
							scope = engine.CreateScope();
							scope.SetVariable("windawesome", windawesome);
							scope.SetVariable("config", config);
						}
						else if (previousLanguage != engine)
						{
							var oldScope = scope;
							scope = engine.CreateScope();
							oldScope.GetItems().
								Where(variable => variable.Value != null).
								ForEach(variable =>	scope.SetVariable(variable.Key, variable.Value));
							previousLanguage.Runtime.Globals.GetItems().
								Where(variable => variable.Value != null).
								ForEach(variable => scope.SetVariable(variable.Key, variable.Value));
						}

						scope = engine.ExecuteFile(file.FullName, scope);
						previousLanguage = engine;
					}
				}
			}
		}
	}

	public enum State
	{
		SHOWN  = 0,
		HIDDEN = 1,
		AS_IS  = 2
	}

	public enum OnWindowShownAction
	{
		SwitchToWindowsWorkspace,
		MoveWindowToCurrentWorkspace,
		TemporarilyShowWindowOnCurrentWorkspace,
		HideWindow
	}

	public class ProgramRule
	{
		internal readonly Regex className;
		internal readonly Regex displayName;
		internal readonly Regex processName;
		internal readonly NativeMethods.WS styleContains;
		internal readonly NativeMethods.WS styleNotContains;
		internal readonly NativeMethods.WS_EX exStyleContains;
		internal readonly NativeMethods.WS_EX exStyleNotContains;
		private readonly CustomMatchingFunction customMatchingFunction;
		internal readonly bool isManaged;
		internal readonly int tryAgainAfter;
		internal readonly int windowCreatedDelay;
		internal readonly bool handleOwnedWindows;
		internal readonly bool hideOwnedPopups;
		internal readonly bool redrawDesktopOnWindowCreated;
		internal readonly OnWindowShownAction onWindowCreatedAction;
		internal readonly OnWindowShownAction onHiddenWindowShownAction;
		internal readonly Rule[] rules;

		public delegate bool CustomMatchingFunction(IntPtr hWnd);

		private static bool DefaultCustomMatchingFunction(IntPtr hWnd)
		{
			return NativeMethods.GetWindow(hWnd, NativeMethods.GW.GW_OWNER) == IntPtr.Zero;
		}

		internal bool IsMatch(IntPtr hWnd, string cName, string dName, string pName, NativeMethods.WS style, NativeMethods.WS_EX exStyle)
		{
			return className.IsMatch(cName) && displayName.IsMatch(dName) && processName.IsMatch(pName) &&
				(style & styleContains) == styleContains && (style & styleNotContains) == 0 &&
				(exStyle & exStyleContains) == exStyleContains && (exStyle & exStyleNotContains) == 0 &&
				customMatchingFunction(hWnd);
		}

		public ProgramRule(string className = ".*", string displayName = ".*", string processName = ".*",
			NativeMethods.WS styleContains = (NativeMethods.WS) 0, NativeMethods.WS styleNotContains = (NativeMethods.WS) 0,
			NativeMethods.WS_EX exStyleContains = (NativeMethods.WS_EX) 0, NativeMethods.WS_EX exStyleNotContains = (NativeMethods.WS_EX) 0,
			CustomMatchingFunction customMatchingFunction = null,

			bool isManaged = true, int tryAgainAfter = -1, int windowCreatedDelay = 0, bool handleOwnedWindows = false,
			bool hideOwnedPopups = true, bool redrawDesktopOnWindowCreated = false,
			OnWindowShownAction onWindowCreatedAction = OnWindowShownAction.SwitchToWindowsWorkspace,
			OnWindowShownAction onHiddenWindowShownAction = OnWindowShownAction.SwitchToWindowsWorkspace,
			int showOnWorkspacesCount = 0, IEnumerable<Rule> rules = null)
		{
			this.className = new Regex(className, RegexOptions.Compiled);
			this.displayName = new Regex(displayName, RegexOptions.Compiled);
			this.processName = new Regex(processName, RegexOptions.Compiled);
			this.styleContains = styleContains;
			this.styleNotContains = styleNotContains;
			this.exStyleContains = exStyleContains;
			this.exStyleNotContains = exStyleNotContains;
			this.customMatchingFunction = customMatchingFunction ?? DefaultCustomMatchingFunction;

			this.isManaged = isManaged;
			if (isManaged)
			{
				this.tryAgainAfter = tryAgainAfter;
				this.windowCreatedDelay = windowCreatedDelay;
				this.handleOwnedWindows = handleOwnedWindows;
				this.hideOwnedPopups = hideOwnedPopups;
				this.redrawDesktopOnWindowCreated = redrawDesktopOnWindowCreated;
				this.onWindowCreatedAction = onWindowCreatedAction;
				this.onHiddenWindowShownAction = onHiddenWindowShownAction;

				if (showOnWorkspacesCount > 0)
				{
					if (rules == null)
					{
						rules = new Rule[] { };
					}

					// This is slow (n ^ 2), but it doesn't matter in this case
					this.rules = rules.Concat(
						Enumerable.Range(1, showOnWorkspacesCount).Where(i => rules.All(r => r.workspace != i)).Select(i => new Rule(i))).
						ToArray();
				}
				else
				{
					this.rules = rules == null ? new[] { new Rule() } : rules.ToArray();
				}
			}
		}

		public class Rule
		{
			public Rule(int workspace = 0, bool isFloating = false, bool showInTabs = true,
				State titlebar = State.AS_IS, State inTaskbar = State.AS_IS, State windowBorders = State.AS_IS,
				bool redrawOnShow = false, bool activateLastActivePopup = true)
			{
				this.workspace = workspace;
				this.isFloating = isFloating;
				this.showInTabs = showInTabs;
				this.titlebar = titlebar;
				this.inTaskbar = inTaskbar;
				this.windowBorders = windowBorders;
				this.redrawOnShow = redrawOnShow;
				this.activateLastActivePopup = activateLastActivePopup;
			}

			internal readonly int workspace;
			internal readonly bool isFloating;
			internal readonly bool showInTabs;
			internal readonly State titlebar;
			internal readonly State inTaskbar;
			internal readonly State windowBorders;
			internal readonly bool redrawOnShow;
			internal readonly bool activateLastActivePopup;
		}
	}
}
