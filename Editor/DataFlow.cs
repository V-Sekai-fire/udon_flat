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
	public Port[][] ports;
	public Port[]   port0;
	public HashSet<string> variables;
	void AnalyzePorts() {
		port0 = new Port[ir.irCode.Length];
		ports = new Port[ir.irCode.Length][];
		
		var outPort0 = new Dictionary<string, (int,int)>();
		var outPorts = new Dictionary<string, List<(int line,int i)>>();
		var queue = new Queue<(int line, int i)>();
		for(int line=0; line<ir.irCode.Length; line++) {
			if(ctrlFlow.jumpTargets.Contains(line))
				outPort0.Clear();

			var instr = ir.irCode[line];
			if(instr.args != null) {
				var linePorts = new Port[instr.args.Length];
				if(instr.opcode == Opcode.EXTERN) {
					var nodeDef = GetNodeDefinition(instr.arg0);
					for(int i=0; i<nodeDef.parameters.Count; i++) {
						if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.OUT)
							linePorts[i].type |= 1;
						if(nodeDef.parameters[i].parameterType != UdonNodeParameter.ParameterType.IN)
							linePorts[i].type |= 2;
					}
					if(instr.arg0 != StatGen.COPY)
						queue.Enqueue((line, -1));
				} else {
					linePorts[0].type |= 1;
					queue.Enqueue((line, -1));
				}

				ports[line] = linePorts;
				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 1) != 0 && outPort0.ContainsKey(instr.args[i]))
						linePorts[i].inputSrc = outPort0[instr.args[i]];
				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 2) != 0) {
						if(!outPorts.ContainsKey(instr.args[i]))
							outPorts[instr.args[i]] = new List<(int,int)>();
						outPorts[instr.args[i]].Add((line, i));
						outPort0[instr.args[i]] = (line, i);
						if(program.SymbolTable.HasExportedSymbol(instr.args[i]))
							queue.Enqueue((line, i));
					}
			}
		}

		variables = new HashSet<string>(program.SymbolTable.GetExportedSymbols().Concat(outPorts.Keys));
		foreach(var ev in eventFromEntry.Values)
			foreach(var name in ev.symbolNames)
				variables.Add(name);

		for(var visited = new HashSet<(int,int)>(); queue.Count > 0; ) {
			var head = queue.Dequeue();
			if(head.i >= 0)
				ports[head.line][head.i].outputRef++;
			else
				port0[head.line].outputRef++;
			if(visited.Contains(head))
				continue;
			visited.Add(head);

			var linePorts = ports[head.line];
			if(head.i >= 0)
				queue.Enqueue((head.line, -1));
			else
				for(int i=0; i<linePorts.Length; i++)
					if((linePorts[i].type & 1) != 0)
						if(linePorts[i].inputSrc != null)
							queue.Enqueue(linePorts[i].inputSrc.Value);
						else {
							var name = ir.irCode[head.line].args[i];
							if(outPorts.ContainsKey(name)) {
								foreach(var tail in outPorts[name])
									queue.Enqueue(tail);
								outPorts.Remove(name);
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