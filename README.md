# UdonFlat

## An experimental Udon assembly to C# decompiler

UdonFlat is the opposite of [UdonSharp](https://github.com/MerlinVR/UdonSharp). It takes [Udon assembly](https://ask.vrchat.com/t/getting-started-with-udon-assembly/84) as input, and produces equivalent C# code with UdonSharp features. This tool works on any Udon asset in your Unity project, since VRCSDK automatically compiles them to Udon assembly. For example, UdonFlat can convert [Udon Node Graph](https://docs.vrchat.com/docs/udon-node-graph-upgrade) to C# code.

**This tool cannot extract Udon assembly from VRChat world. You'll break [VRC Community Guidelines](https://hello.vrchat.com/community-guidelines) if you reverse engineer, steal, extract or rip content from VRChat.**

This decompiler is in an early state. The generated code is not guarenteed to be valid or correct.

## Setup

### Requirements

* [current Unity version supported by VRChat](https://docs.vrchat.com/docs/current-unity-version)
* [VRCSDK3 for worlds](https://vrchat.com/home/download)

UdonSharp is currently not required.

### Getting started

- Import this project into your Assets folder.
- Select a Udon asset, for example, a Udon graph.
- Click `UdonFlat/GenerateCode` from menu.