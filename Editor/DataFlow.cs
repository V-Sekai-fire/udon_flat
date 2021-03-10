using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace SharperUdon {
public class DataFlowAnalyzer {
	public IUdonProgram program;
	public static UdonNodeDefinition GetNodeDefinition(string fullName) {
		return UdonEditorManager.Instance.GetNodeDefinition(fullName);
	}

	public IRGen ir;
	public CtrlFlowAnalyzer ctrlFlow;

	public HashSet<string> volatiles;
	public HashSet<string> variables;
	public int[] writeUseCount;
	public void Analyze() {
		writeUseCount = new int[ir.irCode.Length+1];
		variables = new HashSet<string>(program.SymbolTable.GetExportedSymbols());
		volatiles = new HashSet<string>(program.SymbolTable.GetExportedSymbols());

		var writers = new Dictionary<string, int>();
		for(int line=0; line<ir.irCode.Length; line++) {
			if(ctrlFlow.jumpTargets.Contains(line))
				writers.Clear();

			var instr = ir.irCode[line];
			if(instr.opcode == Opcode.EXTERN) {
				var nodeDef = GetNodeDefinition(instr.arg0);
				for(int i=0; i<nodeDef.parameters.Count; i++)
					if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.OUT) {
						int writer;
						if(writers.TryGetValue(instr.args[i], out writer))
							writeUseCount[writer] ++;
						else
							volatiles.Add(instr.args[i]);
							// symbolFromName[instr.args[i]].collapsible = false;
					}
				for(int i=0; i<nodeDef.parameters.Count; i++)
					if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.IN) {
						writers[instr.args[i]] = i == nodeDef.parameters.Count-1 ? line : ir.irCode.Length+1;
						variables.Add(instr.args[i]);
					}
			} else if(instr.args != null) {
				var i = 0;
				int writer;
				if(writers.TryGetValue(instr.args[i], out writer))
					writeUseCount[writer] ++;
				else
					volatiles.Add(instr.args[i]);
			}
		}
	}
}
}