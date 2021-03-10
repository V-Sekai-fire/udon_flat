using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor.ProgramSources;

namespace SharperUdon {
public class EditorTool {
	[MenuItem("CONTEXT/UdonProgramAsset/GenerateCode")]
	static void GenerateCode(MenuCommand command) {
		var programAsset = (UdonProgramAsset)command.context;
		var serializedProgramAsset = programAsset.SerializedProgramAsset;
		
		var outputPath = System.IO.Path.ChangeExtension(AssetDatabase.GetAssetPath(programAsset), "txt");
		var program = serializedProgramAsset.RetrieveProgram();
		GenerateCode(program, outputPath);
		Debug.Log($"code generated at {outputPath}");
	}
	static void GenerateCode(IUdonProgram program, string outputPath) {
		var decompiler = new Decompiler{program=program};
		decompiler.Init();
		decompiler.Translate();
		using(var writer = System.IO.File.CreateText(outputPath))
			decompiler.GenerateCode(writer);
	}
}
}