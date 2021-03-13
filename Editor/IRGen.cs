using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;

namespace UdonFlat {
public enum Opcode {
	EXTERN, // arg0: signature, args: parameters
	CALL, // arg0: address
	JUMP, // arg0: address, args: falseCond
	EXIT, RETURN, // args: falseCond
}
public class Instruction {
	public string addr;
	public Opcode opcode;
	public string arg0;
	public string[] args;
	public override string ToString() {
		return $"{addr}\t{opcode}\t{arg0}\t{(args == null ? null : string.Join("\t", args))}";
	}
}
public class IRGen {
	public IUdonProgram program;

	public string[][] rawCode;
	public Dictionary<string, int> rawLineFromAddr;
	public void Disassemble() {
		var disassembledProgram = UdonEditorManager.Instance.DisassembleProgram(program);
		rawCode = System.Array.ConvertAll(disassembledProgram, line => Regex.Split(line, "[:,] "));
		rawLineFromAddr = new Dictionary<string, int>();
		for(int line=0; line<rawCode.Length; line++)
			rawLineFromAddr[rawCode[line][0]] = line;
	}

	bool[] visited;
	Instruction[] translated;
	void Translate() {
		visited = new bool[rawCode.Length];
		translated = new Instruction[rawCode.Length];
		foreach(var name in program.EntryPoints.GetSymbols()) {
			var addr = program.EntryPoints.GetAddressFromSymbol(name);
			Translate(rawLineFromAddr[$"0x{addr:X8}"], new Stack<string>());
		}
	}
	void Translate(int line, Stack<string> stack) {
		var prevLine = line;
		while(line < rawCode.Length && !visited[line]) {
			visited[line] = true;

			var instr = rawCode[line];
			var addr = instr[0];
			var opcode = instr[1];
			var nextLine = line+1;
			
			if(opcode == "POP") {
				stack.Pop();
			} else if(opcode == "PUSH") {
				var name = program.SymbolTable.GetSymbolFromAddress(System.Convert.ToUInt32(instr[2], 16));
				stack.Push(name);
			} else if(opcode == "COPY") {
				var targetName = stack.Pop();
				var sourceName = stack.Pop();
				if(sourceName != targetName) // skip no-op
					translated[line] = new Instruction{addr=addr, // express COPY as EXTERN
						opcode=Opcode.EXTERN, arg0=StatGen.COPY, args=new[]{sourceName, targetName}};
			} else if(opcode == "EXTERN") {
				var signature = instr[2].Trim('"');
				var nodeDef = UdonEditorManager.Instance.GetNodeDefinition(signature);
				var paramNames = new string[nodeDef.parameters.Count];
				for(int i=nodeDef.parameters.Count-1; i>=0; i--)
					paramNames[i] = stack.Pop();
				translated[line] = new Instruction{addr=addr, opcode=Opcode.EXTERN, arg0=signature, args=paramNames};
			} else if(opcode == "JUMP" || opcode == "JUMP_IF_FALSE") {
				var jumpAddr = instr[2];
				// match CALL pattern {PUSH label; JUMP xxx; label:}
				var isCall = opcode == "JUMP" && nextLine < rawCode.Length && rawCode[prevLine][1] == "PUSH"
					&& System.Convert.ToUInt32(rawCode[nextLine][0], 16).Equals(
						program.Heap.GetHeapVariable(System.Convert.ToUInt32(rawCode[prevLine][2], 16)));
				if(isCall) {
					translated[line] = new Instruction{addr=addr, opcode=Opcode.CALL, arg0=jumpAddr};
					Translate(rawLineFromAddr[jumpAddr], new Stack<string>(stack)); // explore hidden entry point
					stack.Pop();
				} else {
					var cond = opcode == "JUMP_IF_FALSE" ? new[]{stack.Pop()} : null;
					// out-of-bound address means EXIT
					if(!rawLineFromAddr.TryGetValue(jumpAddr, out var jumpLine)) {
						translated[line] = new Instruction{addr=addr, opcode=Opcode.EXIT, args=cond};
						if(cond == null)
							return;
					} else if(jumpLine != nextLine) { // skip no-op
						translated[line] = new Instruction{addr=addr, opcode=Opcode.JUMP, arg0=jumpAddr, args=cond};
						if(cond == null)
							nextLine = jumpLine;
						else
							Translate(jumpLine, new Stack<string>(stack)); // explore branch
					}
				}
			} else if(opcode == "JUMP_INDIRECT") {
				var jumpName = instr[2];
				// match RETURN pattern {PUSH addr; COPY; JUMP_INDIRECT addr}
				var isReturn = rawCode[prevLine][1] == "COPY" && translated[prevLine] != null
					&& translated[prevLine].args[1] == jumpName;
				if(isReturn)
					translated[prevLine] = null;
				Debug.Assert(isReturn);
				translated[line] = new Instruction{addr=addr, opcode=Opcode.RETURN};
				return;
			} else {
				Debug.Assert(opcode == "NOP" || opcode == "ANNOTATION");
			}
			prevLine = line;
			line = nextLine;
		}
	}

	public Instruction[] irCode;
	public Dictionary<string, int> irLineFromAddr;
	void CollapseLines() {
		var irLine = new int[translated.Length];
		irCode = new Instruction[translated.Count(x => x != null)];
		for(int i=irCode.Length, line=translated.Length-1; line>=0; line--) {
			if(translated[line] != null)
				irCode[--i] = translated[line];
			irLine[line] = i;
		}
		irLineFromAddr = new Dictionary<string, int>();
		for(int line=0; line<rawCode.Length; line++)
			irLineFromAddr[rawCode[line][0]] = irLine[line];
	}
	void CollapseJumps() {
		for(int line=irCode.Length-1; line>=0; line--) {
			var instr = irCode[line];
			if(instr.opcode == Opcode.JUMP) {
				var jumpInstr = irCode[irLineFromAddr[instr.arg0]];
				if((jumpInstr.opcode == Opcode.JUMP || jumpInstr.opcode == Opcode.EXIT || jumpInstr.opcode == Opcode.RETURN) && jumpInstr.args == null) {
					instr.opcode = jumpInstr.opcode;
					instr.arg0 = jumpInstr.arg0;
				}
			}
		}
	}	
	public void Generate() {
		Translate();
		CollapseLines();
		// CollapseJumps(); // TODO: refactoring works better without this
	}

	public string GetRawCode() {
		using(var writer = new System.IO.StringWriter()) {
			foreach(var instr in rawCode)
				writer.WriteLine(string.Join(" ", instr));
			return writer.ToString();
		}
	}
	public string GetIRCode() {
		using(var writer = new System.IO.StringWriter()) {
			foreach(var instr in irCode)
				writer.WriteLine(instr.ToString());
			return writer.ToString();
		}
	}
}
}