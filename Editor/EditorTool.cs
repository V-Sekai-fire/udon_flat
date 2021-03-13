using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor.ProgramSources;

namespace UdonFlat {
public class EditorTool {
	[MenuItem("UdonFlat/GenerateCode", false, 100)]
	public static void GenerateCode() {
		var programAsset = Selection.activeObject as UdonProgramAsset;
		if(programAsset)
			GenerateCode(programAsset);
		else
			Debug.LogError("Please select a UdonProgramAsset");
	}
	[MenuItem("CONTEXT/UdonProgramAsset/GenerateCode")]
	static void GenerateCode(MenuCommand command) {
		GenerateCode((UdonProgramAsset)command.context);
	}
	static void GenerateCode(UdonProgramAsset programAsset) {
		var outputPath = System.IO.Path.ChangeExtension(AssetDatabase.GetAssetPath(programAsset), "cs.txt");
		var program = programAsset.SerializedProgramAsset.RetrieveProgram();
		GenerateCode(program, outputPath, programAsset.name);
		Debug.Log($"code generated at {outputPath}");
	}
	static void GenerateCode(IUdonProgram program, string outputPath, string name) {
		var decompiler = new Decompiler{program=program, name=name};
		decompiler.Init();
		decompiler.Translate();
		using(var writer = System.IO.File.CreateText(outputPath))
			decompiler.GenerateCode(writer);
		UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(outputPath, -1);
	}
}
}