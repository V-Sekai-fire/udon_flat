using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;

namespace UdonFlat {
public enum JumpType {
	None = 0,
	Loop,
	If, Else,
	Break,
	Continue,
}
public class CtrlFlow {
	public IUdonProgram program;
	public IRGen ir;

	public string[] entries;
	public bool[] entryContinue;
	void MarkEntries() {
		entries = new string[ir.code.Length];
		foreach(var name in program.EntryPoints.GetSymbols()) {
			var addr = program.EntryPoints.GetAddressFromSymbol(name);
			entries[ir.lineFromAddr[IRGen.FormatAddr(addr)]] = name;
		}
		foreach(var instr in ir.code)
			if(instr.opcode == Opcode.CALL) {
				var callLine = ir.lineFromAddr[instr.arg0];
				if(entries[callLine] == null)
					entries[callLine] = "";
			}

		entryContinue = new bool[ir.code.Length];
		var entryName = "Init";
		var entryId = 0;
		for(int line=0; line<ir.code.Length; line++)
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
		loopBegin = new int[ir.code.Length];
		for(int line=ir.code.Length-1; line>=0; line--)
			if(ir.code[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.lineFromAddr[ir.code[line].arg0];
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
		choiceEnd = new int[ir.code.Length];
		for(int line=0; line<ir.code.Length; line++)
			if(ir.code[line].opcode == Opcode.JUMP) {
				var jumpLine = ir.lineFromAddr[ir.code[line].arg0];
				if(jumpLine > line) {
					var type = JumpType.If;
					var level = 0;
					if(line+1 < jumpLine && choiceEnd[line+1] == 1 && ir.code[line].args == null) {
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
		for(int line=ir.code.Length-1; line>=0; line--) {
			if(ir.code[line].opcode == Opcode.JUMP) {
				if(jumpTypes[line] == JumpType.Loop)
					loops.Push(line);
				else if(jumpTypes[line] == JumpType.None && loops.Count>0) {
					var jumpLine = ir.lineFromAddr[ir.code[line].arg0];
					var loopLine = loops.Peek();
					if(jumpLine == loopLine+1)
						jumpTypes[line] = JumpType.Break;
					else if(jumpLine == ir.lineFromAddr[ir.code[loopLine].arg0])
						jumpTypes[line] = JumpType.Continue;
				}
			}
			for(int i=loopBegin[line]; i>0; i--)
				loops.Pop();
		}
	}

	public string[] labels;
	void MarkLabels() {
		labels = new string[ir.code.Length];
		foreach(var instr in ir.code)
			if(instr.opcode == Opcode.JUMP && jumpTypes[ir.lineFromAddr[instr.addr]] == JumpType.None) {
				var jumpLine = ir.lineFromAddr[instr.arg0];
				if(labels[jumpLine] == null)
					labels[jumpLine] = $"label_{jumpLine}";
			}
	}

	public void Analyze() {
		MarkEntries();

		jumpTypes = new JumpType[ir.code.Length];
		LocateLoops();
		LocateChoices();
		LocateBreaks();
		MarkLabels();
	}
}
}