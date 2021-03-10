using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;

namespace SharperUdon {
public enum CtrlFlowType {
	None = 0,
	Loop,
	If, Else,
	Break,
	Continue,
}
public class CtrlFlowAnalyzer {
	public IUdonProgram program;
	public IRGen ir;
	void CollapseJumps() {
		for(int line=ir.irCode.Length-1; line>=0; line--) {
			var instr = ir.irCode[line];
			if(instr.opcode == Opcode.JUMP) {
				var jumpInstr = ir.irCode[ir.irLineFromAddr[instr.arg0]];
				if((jumpInstr.opcode == Opcode.JUMP || jumpInstr.opcode == Opcode.EXIT) && jumpInstr.args == null) {
					instr.opcode = jumpInstr.opcode;
					instr.arg0 = jumpInstr.arg0;
				}
			}
		}
	}	
	
	public CtrlFlowType[] types;

	public int[] loopBegin;
	void CreateLoops() {
		loopBegin = new int[ir.irCode.Length];
		for(int line=ir.irCode.Length-1; line>=0; line--)
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
				if(jumpLine <= line) {
					var level = 0;
					for(int i=line; i>jumpLine && level>=0; i--) 
						level -= loopBegin[i];
					if(level == 0) {
						types[line] = CtrlFlowType.Loop;
						loopBegin[jumpLine] ++;
					}
				}
			}
	}

	public int[] choiceEnd;
	void CreateChoices() {
		choiceEnd = new int[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++)
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
				if(jumpLine > line) {
					var type = CtrlFlowType.If;
					var level = 0;
					if(line+1 < jumpLine && choiceEnd[line+1] == 1 && ir.irCode[line].args == null) {
						type = CtrlFlowType.Else;
						level = 1;
					}
					for(int i=line+1; i<jumpLine && level>=0; i++) {
						level -= choiceEnd[i];
						if(level < 0)
							break;
						level += loopBegin[i];
						if(types[i] == CtrlFlowType.Loop)
							level --;
					}
					if(level == 0) {
						types[line] = type;
						choiceEnd[jumpLine] ++;
						if(type == CtrlFlowType.Else)
							choiceEnd[line+1] --;
					}
				}
			}
	}

	void CreateBreaks() {
		var loops = new Stack<int>();
		for(int line=ir.irCode.Length-1; line>=0; line--) {
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				if(types[line] == CtrlFlowType.Loop)
					loops.Push(line);
				else if(types[line] == CtrlFlowType.None && loops.Count>0) {
					var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
					var loopLine = loops.Peek();
					if(jumpLine == loopLine+1)
						types[line] = CtrlFlowType.Break;
					else if(jumpLine == ir.irLineFromAddr[ir.irCode[loopLine].arg0])
						types[line] = CtrlFlowType.Continue;
				}
			}
			for(int i=loopBegin[line]; i>0; i--)
				loops.Pop();
		}
	}

	public string[] entries;
	void CreateEntries() {
		entries = new string[ir.irCode.Length];
		foreach(var name in program.EntryPoints.GetSymbols()) {
			var addr = program.EntryPoints.GetAddressFromSymbol(name);
			entries[ir.irLineFromAddr[$"0x{addr:X8}"]] = name;
		}
		foreach(var instr in ir.irCode)
			if(instr.opcode == Opcode.CALL) {
				var jumpLine = ir.irLineFromAddr[instr.arg0];
				if(entries[jumpLine] == null)
					entries[jumpLine] = $"entry_{jumpLine}";
			}
	}

	public string[] labels;
	void CreateLabels() {
		labels = new string[ir.irCode.Length];
		foreach(var instr in ir.irCode)
			if(instr.opcode == Opcode.JUMP && types[ir.irLineFromAddr[instr.addr]] == CtrlFlowType.None) {
				var jumpLine = ir.irLineFromAddr[instr.arg0];
				if(labels[jumpLine] == null)
					labels[jumpLine] = $"label_{jumpLine}";
			}
	}

	public HashSet<int> jumpTargets;
	void FindJumpTargets() {
		var reUdonCall = new Regex(@"\.__SendCustom(Network)?Event");
		jumpTargets = new HashSet<int>();
		foreach(var instr in ir.irCode) {
			int line = ir.irLineFromAddr[instr.addr];
			if(entries[line] != null)
				jumpTargets.Add(line);
			if(instr.opcode == Opcode.CALL || instr.opcode == Opcode.JUMP)
				jumpTargets.Add(ir.irLineFromAddr[instr.arg0]);
			if(instr.opcode == Opcode.CALL || instr.opcode == Opcode.EXTERN && reUdonCall.IsMatch(instr.arg0))
				jumpTargets.Add(line+1);
		}
	}
	public void Analyze() {
		CollapseJumps();

		types = new CtrlFlowType[ir.irCode.Length];
		CreateEntries();
		CreateLoops();
		CreateChoices();
		CreateBreaks();
		CreateLabels();
		FindJumpTargets();
	}
}
}