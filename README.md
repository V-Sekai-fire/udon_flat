# SharperUdon

## An experimental decompiler from Udon assembly to C#

SharperUdon is a compiler that decompiles Udon assembly to C# (UdonSharp). For example, it can be used to convert Udon Graph to UdonSharp code, since VRCSDK always compiles Udon Graph to Udon assembly.

This tool can only decompile Udon assembly in your Unity project. It cannot extract Udon assembly from VRChat world. **You should not reverse engineer, steal, extract or rip content from VRChat.**

This decompiler is in an early state. The generated code is not guarenteed to be correct.

## How to use

- Import this project into your Assets folder.
- Select a Udon Graph asset.
- Click `SharperUdon/GenerateCode` from menu.