using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;

namespace SharperUdon {
public enum JumpType {
	None = 0,
	Loop,
	If, Else,
	Break,
	Continue,
}
public class CtrlFlowAnalyzer {
	public IUdonProgram program;
	public IRGen ir;

	public string[] entries;
	public bool[] entryContinue;
	void MarkEntries() {
		entries = new string[ir.irCode.Length];
		foreach(var name in program.EntryPoints.GetSymbols()) {
			var addr = program.EntryPoints.GetAddressFromSymbol(name);
			entries[ir.irLineFromAddr[$"0x{addr:X8}"]] = name;
		}
		foreach(var instr in ir.irCode)
			if(instr.opcode == Opcode.CALL) {
				var callLine = ir.irLineFromAddr[instr.arg0];
				if(entries[callLine] == null)
					entries[callLine] = "";
			}

		entryContinue = new bool[ir.irCode.Length];
		var entryName = "Init";
		var entryId = 0;
		for(int line=0; line<ir.irCode.Length; line++)
			if(entries[line] == "") {
				entries[line] = $"{entryName}_{entryId}";
				entryId ++;
				entryContinue[line] = true;
			} else if(entries[line] != null) {
				entryName = entries[line];
				entryId = 0;
			}
	}

	public JumpType[] jumpTypes;

	public int[] loopBegin;
	void LocateLoops() {
		loopBegin = new int[ir.irCode.Length];
		for(int line=ir.irCode.Length-1; line>=0; line--)
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
				if(jumpLine <= line) {
					var level = 0;
					for(int i=line; i>jumpLine && level>=0; i--) 
						level -= loopBegin[i];
					if(level == 0) {
						jumpTypes[line] = JumpType.Loop;
						loopBegin[jumpLine] ++;
					}
				}
			}
	}

	public int[] choiceEnd;
	void LocateChoices() {
		choiceEnd = new int[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++)
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
				if(jumpLine > line) {
					var type = JumpType.If;
					var level = 0;
					if(line+1 < jumpLine && choiceEnd[line+1] == 1 && ir.irCode[line].args == null) {
						type = JumpType.Else;
						level = 1;
					}
					for(int i=line+1; i<jumpLine && level>=0; i++) {
						level -= choiceEnd[i];
						if(level < 0)
							break;
						level += loopBegin[i];
						if(jumpTypes[i] == JumpType.Loop)
							level --;
					}
					if(level == 0) {
						jumpTypes[line] = type;
						choiceEnd[jumpLine] ++;
						if(type == JumpType.Else)
							choiceEnd[line+1] --;
					}
				}
			}
	}

	void LocateBreaks() {
		var loops = new Stack<int>();
		for(int line=ir.irCode.Length-1; line>=0; line--) {
			if(ir.irCode[line].opcode == Opcode.JUMP) {
				if(jumpTypes[line] == JumpType.Loop)
					loops.Push(line);
				else if(jumpTypes[line] == JumpType.None && loops.Count>0) {
					var jumpLine = ir.irLineFromAddr[ir.irCode[line].arg0];
					var loopLine = loops.Peek();
					if(jumpLine == loopLine+1)
						jumpTypes[line] = JumpType.Break;
					else if(jumpLine == ir.irLineFromAddr[ir.irCode[loopLine].arg0])
						jumpTypes[line] = JumpType.Continue;
				}
			}
			for(int i=loopBegin[line]; i>0; i--)
				loops.Pop();
		}
	}

	public string[] labels;
	void MarkLabels() {
		labels = new string[ir.irCode.Length];
		foreach(var instr in ir.irCode)
			if(instr.opcode == Opcode.JUMP && jumpTypes[ir.irLineFromAddr[instr.addr]] == JumpType.None) {
				var jumpLine = ir.irLineFromAddr[instr.arg0];
				if(labels[jumpLine] == null)
					labels[jumpLine] = $"label_{jumpLine}";
			}
	}

	public HashSet<int> jumpTargets;
	void MarkJumpTargets() {
		var reUdonCall = new Regex(@"\.__SendCustom(Network)?Event");
		jumpTargets = new HashSet<int>();
		foreach(var instr in ir.irCode) {
			int line = ir.irLineFromAddr[instr.addr];
			if(entries[line] != null)
				jumpTargets.Add(line);
			if(instr.opcode == Opcode.CALL || instr.opcode == Opcode.JUMP)
				jumpTargets.Add(ir.irLineFromAddr[instr.arg0]);
			if(instr.opcode == Opcode.CALL || (instr.opcode == Opcode.EXTERN && reUdonCall.IsMatch(instr.arg0)))
				jumpTargets.Add(line+1);
		}
	}
	public void Analyze() {
		MarkEntries();

		jumpTypes = new JumpType[ir.irCode.Length];
		LocateLoops();
		LocateChoices();
		LocateBreaks();
		MarkLabels();
		MarkJumpTargets();
	}
}
}