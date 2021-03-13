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
	public IRGen ir;
	public CtrlFlowAnalyzer ctrlFlow;

	public class EventEntry {
		public string eventName;
		public string entryName;
		public string[] symbolNames;
		public UdonNodeDefinition nodeDef;
	}
	public Dictionary<string, EventEntry> eventFromEntry;
	void GetEvents() {
		eventFromEntry = new Dictionary<string, EventEntry>();
		var nodeDefs = UdonEditorManager.Instance.GetNodeDefinitions();
		foreach(var nodeDef in nodeDefs) {
			var m = Regex.Match(nodeDef.fullName, @"^Event_(\w+)");
			if(!m.Success)
				continue;
			var eventName = m.Groups[1].Value;
			var entryName = "_" + eventName.Substring(0,1).ToLowerInvariant() + eventName.Substring(1);
			if(!program.EntryPoints.HasExportedSymbol(entryName))
				continue;

			var eventEntry = new EventEntry{eventName=eventName, entryName=entryName, nodeDef=nodeDef,
				symbolNames=new string[nodeDef.parameters.Count]};
			for(int i=0; i<nodeDef.parameters.Count; i++) {
				var p = nodeDef.parameters[i];
				eventEntry.symbolNames[i] = entryName.Substring(1)
					+ p.name.Substring(0,1).ToUpperInvariant() + p.name.Substring(1);
			}
			eventFromEntry[entryName] = eventEntry;
		}
	}

	public struct Port {
		public int type;
		public int outputRef;
		public (int,int)? inputSrc;
	}
	public Port[]   port0;
	public Port[][] ports;
	public HashSet<string> variables;
	void AnalyzePorts() {
		port0 = new Port[ir.irCode.Length];
		ports = new Port[ir.irCode.Length][];
		
		var epoch = 0;
		var queue = new Queue<(int line, int i)>();
		var outRecent = new Dictionary<string, ((int,int),int)>();
		var outEscape = new Dictionary<string, List<(int,int)>>();
		for(int line=0; line<ir.irCode.Length; line++) {
			var instr = ir.irCode[line];
			if(instr.args != null) {
				var linePorts = new Port[instr.args.Length];
				if(instr.opcode == Opcode.EXTERN) {
					var nodeDef = UdonEditorManager.Instance.GetNodeDefinition(instr.arg0);
					for(int i=0; i<nodeDef.parameters.Count; i++) {
						if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.OUT)
							linePorts[i].type |= 1;
						if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.IN)
							linePorts[i].type |= 2;
					}
					if(instr.arg0 != StatGen.COPY)
						queue.Enqueue((line, -1)); // proper EXTERN is volatile
				} else {
					linePorts[0].type |= 1;
					queue.Enqueue((line, -1)); // conditional statement is volatile
				}
				ports[line] = linePorts;

				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 1) != 0)
						if(outRecent.TryGetValue(instr.args[i], out var portTime))
							linePorts[i].inputSrc = portTime.Item1;
				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 2) != 0) {
						var symbol = instr.args[i];
						if(outRecent.TryGetValue(symbol, out var portTime) && portTime.Item2 < epoch) {
							if(!outEscape.TryGetValue(symbol, out var lst))
								outEscape[symbol] = lst = new List<(int,int)>();
							lst.Add(portTime.Item1); // flush lazy escape
						}
						outRecent[symbol] = ((line, i), epoch);
						if(program.SymbolTable.HasExportedSymbol(symbol))
							queue.Enqueue((line, i)); // exported symbol is volatile
					}
			}

			// escape after jump sources & before jump targets
			if(instr.opcode != Opcode.EXTERN)
				epoch ++; // lazy escape
			if(ctrlFlow.jumpTargets.Contains(line+1) || line+1 == ir.irCode.Length) {
				foreach(var symbolPortTime in outRecent) {
					if(!outEscape.TryGetValue(symbolPortTime.Key, out var lst))
						outEscape[symbolPortTime.Key] = lst = new List<(int,int)>();
					lst.Add(symbolPortTime.Value.Item1); // flush lazy escape
				}
				outRecent.Clear();
			}
		}

		variables = new HashSet<string>(program.SymbolTable.GetExportedSymbols().Concat(outEscape.Keys));
		foreach(var eventEntry in eventFromEntry.Values)
			foreach(var name in eventEntry.symbolNames)
				variables.Add(name);

		// BFS ports from volatiles
		for(var visited = new HashSet<(int,int)>(); queue.Count > 0; ) {
			var head = queue.Dequeue();
			if(head.i >= 0) // output port
				ports[head.line][head.i].outputRef++;
			else // statement port
				port0[head.line].outputRef++;
			if(visited.Contains(head))
				continue;
			visited.Add(head);

			var linePorts = ports[head.line];
			if(head.i >= 0) // output port
				queue.Enqueue((head.line, -1));
			else // statement port
				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 1) != 0)
						if(linePorts[i].inputSrc != null)
							queue.Enqueue(linePorts[i].inputSrc.Value);
						else { // escaped input port
							var symbol = ir.irCode[head.line].args[i];
							if(outEscape.TryGetValue(symbol, out var lst)) {
								outEscape.Remove(symbol); // do it once
								foreach(var tail in lst)
									queue.Enqueue(tail);
							}
						}
		}
		// for(int line=0; line<ir.irCode.Length; line++) {
		// 	var linePorts = ports[line];
		// 	if(linePorts != null)
		// 		Debug.Log($"{ir.irCode[line].addr}: {string.Join(", ", linePorts.Select(x => $"{((x.type&2) != 0 ? x.outputRef : -1)}"))} : {port0[line].outputRef} {ctrlFlow.jumpTargets.Contains(line)}");
		// }
	}

	public void Analyze() {
		GetEvents();
		AnalyzePorts();
	}
}
}