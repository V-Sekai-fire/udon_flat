# UdonFlat

## An experimental Udon assembly to C# decompiler

UdonFlat does the opposite of [UdonSharp](https://github.com/MerlinVR/UdonSharp). It takes [Udon assembly](https://ask.vrchat.com/t/getting-started-with-udon-assembly/84) as input, and generates equivalent and human-readable C# code with UdonSharp features.

UdonFlat can convert [Udon Node Graph](https://docs.vrchat.com/docs/udon-node-graph-upgrade) to C# code. It also works on other types of Udon assets imported in your Unity project, since VRCSDK automatically compiles them to Udon assembly.

This tool cannot extract Udon assembly from VRChat worlds.

## Requirements

* [Current Unity version supported by VRChat](https://docs.vrchat.com/docs/current-unity-version)
* [VRCSDK3-Worlds](https://vrchat.com/home/download)

UdonSharp is currently not required.

## Getting started

- Import this project into your Assets folder.
- Select a Udon asset, for example, a Udon graph.
- Click `UdonFlat/GenerateCode` from top menu or inspector dropdown.
- A text file of generated code will pop up.

![guide](../../wikis/uploads/dd194998e47d329a2dcd39e30cedb81e/guide.png)
