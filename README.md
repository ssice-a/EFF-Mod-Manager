# EFF Mod Manager

Windows application for installing EFF, managing local Mods, and repairing older Mod packages. This repository keeps its own source history and Release workflow.

The unified EFF architecture, shared compiler package and user-selected index paths are documented in the [EFF main repository](https://github.com/ssice-a/EFF), especially [Shared Tools and Indexes](https://github.com/ssice-a/EFF/blob/main/docs/shared-tools-and-indexes.md).

Build and test with the .NET 9 Windows SDK. Repair uses the compiler published by EFF and caches it below the user's selected resource root; the complete index remains in the AnimeStudio workspace.
