using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Editor;

namespace UdonFlat {
public class Disassembler {
	public IUdonProgram program;

	public string[][] code;
	public Dictionary<string, int> lineFromAddr;
	public void Disassemble() {
		code = System.Array.ConvertAll(UdonEditorManager.Instance.DisassembleProgram(program),
			line => Regex.Split(line, "[:,] ", RegexOptions.Compiled));
		lineFromAddr = new Dictionary<string, int>();
		for(int line=0; line<code.Length; line++)
			lineFromAddr[code[line][0]] = line;
	}
}
}