using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using System.CodeDom.Compiler;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace UdonFlat {
public class Symbol {
	public string name;
	public uint addr;
	public System.Type type;
	public object value;

	public bool exported;
	public System.Type caster;
	public CodeExpression constExpr;
	
	string prettyName;
	UdonSyncInterpolationMethod? syncInterp;

	public Symbol(string name, IUdonSymbolTable table, IUdonHeap heap, IUdonSyncMetadataTable syncTable, bool isConst) {
		this.name = name;
		addr = table.GetAddressFromSymbol(name);
		type = table.GetSymbolType(name);
		value = heap?.GetHeapVariable(addr);

		exported = table.HasExportedSymbol(name);

		prettyName = unmangleName(name, addr); // TODO: sharedSymbols

		if(isConst) {
			constExpr = ExprGen.Ref(value);
			var valueStr = value as string;
			if(valueStr != null && valueStr.Contains('\n'))
				constExpr = null;
		}

		if(type.IsArray && value != null)
			caster = getCaster(value);

		var syncProps = syncTable.GetSyncMetadataFromSymbol(name)?.Properties;
		if(syncProps != null)
			syncInterp = syncProps[0].InterpolationAlgorithm;
	}
	public override string ToString() {
		return prettyName;
	}
	public CodeMemberField BuildMember() {
		var field = new CodeMemberField(ExprGen.Type(type), ToString());
		field.Attributes = MemberAttributes.Final;
		if(exported)
			field.Attributes |= MemberAttributes.Public;
		else
			field.Attributes |= MemberAttributes.Private;
		if(!(value == null || (type.IsValueType && System.Activator.CreateInstance(type).Equals(value))))
			field.InitExpression = ExprGen.Value(value);
		if(syncInterp != null) {
			field.CustomAttributes = new CodeAttributeDeclarationCollection();
			field.CustomAttributes.Add(new CodeAttributeDeclaration("UdonSharp.UdonSynced",
				new CodeAttributeArgument(new CodeFieldReferenceExpression(
					new CodeTypeReferenceExpression("UdonSharp.UdonSyncMode"),
					syncInterp.Value.ToString()))));
		}
		return field;
	}
	static string unmangleName(string name, uint addr) {
		var m = Regex.Match(name, @"^__\d+_(.+)_\w+$");
		if(m.Success)
			return $"_{m.Groups[1].Value}_{addr}";
		return name;
	}
	static System.Type getCaster(object value) {
		var arr = (System.Array)value;
		var n = arr.GetLength(0);
		if(n == 0)
			return null;
		if(arr.GetValue(0) == null)
			return null;
		var elemType = arr.GetValue(0).GetType();
		if(!elemType.IsEnum)
			return null;
		for(int i=0; i<n; i++)
			if(!System.Enum.ToObject(elemType, i).Equals(arr.GetValue(i)))
				return null;
		return elemType;
	}
}
public class Entry {
	public string name;

	public bool exported;

	string prettyName;
	DataFlow.EventEntry eventEntry;

	public Entry(string name, IUdonSymbolTable table, DataFlow.EventEntry eventEntry) {
		this.name = name;
		this.eventEntry = eventEntry;

		exported = table.HasExportedSymbol(name);
		prettyName = name;
		if(eventEntry != null)
			prettyName = eventEntry.eventName;
	}
	public override string ToString() {
		return prettyName;
	}
	public CodeMemberMethod BuildMember() {
		var method = new CodeMemberMethod();
		method.Name = ToString();
		method.Attributes = MemberAttributes.Final;
		if(exported)
			method.Attributes |= MemberAttributes.Public;
		else
			method.Attributes |= MemberAttributes.Private;

		if(eventEntry != null) {
			if(!monoBehaviourMessages.Contains(eventEntry.eventName)) {
				method.Attributes &= ~MemberAttributes.Final;
				method.Attributes |= MemberAttributes.Override;
			}
			for(int i=0; i<eventEntry.symbolNames.Length; i++)
				method.Parameters.Add(new CodeParameterDeclarationExpression(
					ExprGen.Type(eventEntry.nodeDef.parameters[i].type), eventEntry.symbolNames[i]));
		}
		return method;
	}
	// https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
	static HashSet<string> monoBehaviourMessages = new HashSet<string>{
"Awake", "FixedUpdate", "LateUpdate", "OnAnimatorIK", "OnAnimatorMove",
"OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit", "OnAudioFilterRead",
"OnBecameInvisible", "OnBecameVisible", "OnCollisionEnter", "OnCollisionEnter2D",
"OnCollisionExit", "OnCollisionExit2D", "OnCollisionStay", "OnCollisionStay2D",
"OnConnectedToServer", "OnControllerColliderHit", "OnDestroy", "OnDisable", "OnDisconnectedFromServer",
"OnDrawGizmos", "OnDrawGizmosSelected", "OnEnable", "OnFailedToConnect", "OnFailedToConnectToMasterServer",
"OnGUI", "OnJointBreak", "OnJointBreak2D", "OnMasterServerEvent", "OnMouseDown", "OnMouseDrag",
"OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseUp", "OnMouseUpAsButton", "OnNetworkInstantiate",
"OnParticleCollision", "OnParticleSystemStopped", "OnParticleTrigger", "OnPlayerConnected", "OnPlayerDisconnected",
"OnPostRender", "OnPreCull", "OnPreRender", "OnRenderImage", "OnRenderObject", "OnSerializeNetworkView",
"OnServerInitialized", "OnTransformChildrenChanged", "OnTransformParentChanged",
"OnTriggerEnter", "OnTriggerEnter2D", "OnTriggerExit", "OnTriggerExit2D", "OnTriggerStay", "OnTriggerStay2D",
"OnValidate", "OnWillRenderObject", "Reset", "Start", "Update"
	};
}
public class Decompiler {
	public IUdonProgram program;
	public string name = "Unnamed";

	public static UdonNodeDefinition GetNodeDefinition(string fullName) {
		return UdonEditorManager.Instance.GetNodeDefinition(fullName);
	}

	public IRGen ir;
	public CtrlFlow ctrlFlow;
	public DataFlow dataFlow;

	public Symbol[] symbols;
	public List<Entry> entries;
	public Dictionary<string, Symbol> symbolFromName;
	public Dictionary<string, Entry> entryFromName;
	public void InitSymbols() {
		var heap = program.Heap;
		var symbolTable = program.SymbolTable;
		var symbolNames = symbolTable.GetSymbols().ToArray();
		var syncTable = program.SyncMetadataTable;
		var entryPoints = program.EntryPoints;

		symbols = new Symbol[symbolNames.Length];
		symbolFromName = new Dictionary<string, Symbol>();
		for(int i=0; i<symbolNames.Length; i++) {
			var symbol = new Symbol(symbolNames[i],
				symbolTable, heap, syncTable, !dataFlow.mutableSymbols.Contains(symbolNames[i]));
			symbols[i] = symbol;
			symbolFromName[symbol.name] = symbol;
		}

		entries = new List<Entry>();
		entryFromName = new Dictionary<string, Entry>();
		foreach(var entryName in ctrlFlow.entries)
			if(entryName != null) {
				dataFlow.eventFromEntry.TryGetValue(entryName, out var eventEntry);
				var entry = new Entry(entryName, entryPoints, eventEntry);
				entries.Add(entry);
				entryFromName[entry.name] = entry;
			}
	}

	public void Init() {
		ir = new IRGen{program=program};
		ir.Disassemble();
		ir.Generate();

		ctrlFlow = new CtrlFlow{program=program, ir=ir};
		ctrlFlow.Analyze();

		dataFlow = new DataFlow{program=program, ir=ir, ctrlFlow=ctrlFlow};
		dataFlow.Analyze();

		InitSymbols();
	}

	int? getAssignLeftRefCount(int line, CodeAssignStatement assign) {
		if(!(assign.Left is CodeVariableReferenceExpression))
			return null;
		return dataFlow.ports[line][dataFlow.ports[line].Length-1].outRefCount;
	}
	bool substituteInPorts(int line, CodeExpression[] portExprs) {
		if(dataFlow.port0[line].outRefCount == 0)
			return false;

		var linePorts = dataFlow.ports[line];
		for(int i=0; i<portExprs.Length; i++)
			if((linePorts[i].type & 1) != 0) {
				var symbol = symbolFromExpr[(CodeVariableReferenceExpression)portExprs[i]];
				if(symbol.constExpr != null) {
					portExprs[i] = symbol.constExpr;
					Debug.Assert(linePorts[i].inSource == null);
				}
			}

		for(int i=portExprs.Length-1, prevLine=line-1; prevLine>=0; prevLine--) {
			if(statements[prevLine] == null)
				continue;
			var prevAssign = statements[prevLine] as CodeAssignStatement;
			if(!(prevAssign != null && getAssignLeftRefCount(prevLine, prevAssign) == 1))
				break;
			var found = false;
			for(; i>=0 && !found; i--)
				if(linePorts[i].inSource == (prevLine, dataFlow.ports[prevLine].Length-1)) {
					portExprs[i] = prevAssign.Right;
					statements[prevLine] = null;
					found = true;
				}
			if(!found)
				break;
		}
		return true;
	}
	void simplifyExtern(int line) {
		var assign = statements[line] as CodeAssignStatement;
		if(assign != null) {
			switch(assign.Right) {
			case CodeArrayIndexerExpression indexer: // undo U# enum lookup table
				if(indexer.TargetObject is CodeVariableReferenceExpression) {
					var symbol = symbolFromExpr[(CodeVariableReferenceExpression)indexer.TargetObject];
					if(symbol.caster != null && indexer.Indices.Count == 1)
						 assign.Right = new CodeCastExpression(ExprGen.Type(symbol.caster), indexer.Indices[0]);
				}
				break;
			}
			if(getAssignLeftRefCount(line, assign) == 0)
				statements[line] = new CodeExpressionStatement(assign.Right);
		}
	}

	public Dictionary<CodeVariableReferenceExpression, Symbol> symbolFromExpr;
	public CodeStatement[] statements;
	Dictionary<string, CodeVariableReferenceExpression> entryExprs;

	public void Translate() {
		var symbolExprs = new Dictionary<string, CodeVariableReferenceExpression>();
		symbolFromExpr = new Dictionary<CodeVariableReferenceExpression, Symbol>();
		foreach(var symbol in symbols) {
			symbolExprs[symbol.name] = new CodeVariableReferenceExpression(symbol.ToString());
			symbolFromExpr[symbolExprs[symbol.name]] = symbol;
		}

		entryExprs = new Dictionary<string, CodeVariableReferenceExpression>();
		foreach(var entry in entries)
			entryExprs[entry.name] = new CodeVariableReferenceExpression(entry.ToString());

		statements = new CodeStatement[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++) {
			var instr = ir.irCode[line];
			var portExprs = default(CodeExpression[]);
			if(dataFlow.ports[line] != null) {
				portExprs = System.Array.ConvertAll(instr.args, s => (CodeExpression)symbolExprs[s]);
				if(!substituteInPorts(line, portExprs))
					continue;
			}
			if(instr.opcode == Opcode.EXTERN) {
				if(dataFlow.port0[line].outRefCount == 0)
					continue;
				var nodeDef = GetNodeDefinition(instr.arg0);
				statements[line] = StatGen.Extern(nodeDef, portExprs);
				if(instr.arg0 == StatGen.COPY) { // COPY casting
					var assign = (CodeAssignStatement)statements[line];
					var sourceType = symbolFromName[instr.args[0]].type;
					var targetType = symbolFromName[instr.args[1]].type;
					if(!targetType.IsAssignableFrom(sourceType))
						assign.Right = new CodeCastExpression(ExprGen.Type(targetType), assign.Right);
				}
				simplifyExtern(line);
			} else if(instr.opcode == Opcode.CALL) {
				Debug.Assert(instr.args == null);
				statements[line] = new CodeExpressionStatement(new CodeDelegateInvokeExpression(
					entryExprs[ctrlFlow.entries[ir.irLineFromAddr[instr.arg0]]]));
			} else if(instr.opcode == Opcode.JUMP || instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN) {
				var condExpr = portExprs == null ? ExprGen.True : ExprGen.Not(portExprs[0]);
				if(ctrlFlow.jumpTypes[line] == JumpType.Loop)
					statements[line] = StatGen.If(ExprGen.Not(condExpr), StatGen.Break);
				else if(ctrlFlow.jumpTypes[line] == JumpType.If)
					statements[line] = new CodeConditionStatement(ExprGen.Not(condExpr));
				else if(ctrlFlow.jumpTypes[line] == JumpType.Else)
					statements[line] = new CodeConditionStatement(); // stub
				else {
					if(ctrlFlow.jumpTypes[line] == JumpType.Break)
						statements[line] = StatGen.Break;
					else if(ctrlFlow.jumpTypes[line] == JumpType.Continue)
						statements[line] = StatGen.Continue;
					else if(instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN)
						statements[line] = StatGen.Return;
					else
						statements[line] = new CodeGotoStatement(ctrlFlow.labels[ir.irLineFromAddr[instr.arg0]]);
					statements[line] = StatGen.If(condExpr, statements[line]);
				}
			} else {
				Debug.LogError(instr);
			}
		}
	}
	void simplifyCond(CodeConditionStatement cond, CodeStatementCollection scope) {
		if(cond.TrueStatements.Count == 0) {
			cond.Condition = ExprGen.Not(cond.Condition);
			cond.TrueStatements.AddRange(cond.FalseStatements);
			cond.FalseStatements.Clear();
		}
		if(cond.TrueStatements.Count == 1 && cond.FalseStatements.Count == 0) {
			var assign = cond.TrueStatements[0] as CodeAssignStatement;
			var op = assign?.Right as CodeBinaryOperatorExpression;
			if(op != null && assign.Left == op.Left) {
				var shortCircuit = 
					op.Operator == CodeBinaryOperatorType.BooleanAnd ? op.Left == cond.Condition :
					op.Operator == CodeBinaryOperatorType.BooleanOr && op.Left == ExprGen.Not(cond.Condition);
				if(shortCircuit) {
					scope[scope.Count-1] = assign;
					if(scope.Count >= 2) {
						var assign2 = scope[scope.Count-2] as CodeAssignStatement;
						if(assign2?.Left == assign.Left) {
							op.Left = assign2.Right;
							scope.RemoveAt(scope.Count-2);
						}
					}
				}
			}
		}
	}
	void simplifyIter(CodeIterationStatement iter, CodeStatementCollection scope) {
		if(iter.Statements.Count == 0)
			return;
		var cond = iter.Statements[0] as CodeConditionStatement;
		if(cond != null && cond.TrueStatements.Count == 1 && cond.FalseStatements.Count == 0)
			if(cond.TrueStatements[0] == StatGen.Break) {
				iter.Statements.RemoveAt(0);
				iter.TestExpression = ExprGen.Not(cond.Condition);
			}
		var iterVar = (iter.TestExpression as CodeBinaryOperatorExpression)?.Left;
		if(iterVar != null) {
			if(iter.Statements.Count > 0) {
				var assign = iter.Statements[iter.Statements.Count-1] as CodeAssignStatement;
				if(assign?.Left == iterVar) {
					iter.Statements.RemoveAt(iter.Statements.Count-1);
					iter.IncrementStatement = assign;
				}
			}
			if(scope.Count > 1) {
				var assign = scope[scope.Count-2] as CodeAssignStatement;
				if(assign?.Left == iterVar) {
					scope.RemoveAt(scope.Count-2);
					iter.InitStatement = assign;
				}
			}
		}
	}
	public int BuildFunc(int lineBegin, CodeStatementCollection scope0) {
		var statFromScope = new Dictionary<CodeStatementCollection, CodeStatement>();
		var scopes = new Stack<CodeStatementCollection>(new[]{scope0});
		int line = lineBegin;
		for(; line<ir.irCode.Length && (ctrlFlow.entries[line] == null || line==lineBegin); line++) {
			for(int i=ctrlFlow.choiceEnd[line]; i>0; i--)
				simplifyCond((CodeConditionStatement)statFromScope[scopes.Pop()], scopes.Peek());
			if(ctrlFlow.labels[line] != null)
				scopes.Peek().Add(new CodeLabeledStatement(ctrlFlow.labels[line]));
			for(int i=ctrlFlow.loopBegin[line]; i>0; i--) {
				var iter = StatGen.While();
				statFromScope[iter.Statements] = iter;
				scopes.Peek().Add(iter);
				scopes.Push(iter.Statements);
			}
			if(ctrlFlow.jumpTypes[line] == JumpType.Loop) {
				if(statements[line] != null)
					scopes.Peek().Add(statements[line]);
				simplifyIter((CodeIterationStatement)statFromScope[scopes.Pop()], scopes.Peek());
			} else if(ctrlFlow.jumpTypes[line] == JumpType.If) {
				var cond = (CodeConditionStatement)statements[line];
				statFromScope[cond.TrueStatements] = cond;
				statFromScope[cond.FalseStatements] = cond;
				scopes.Peek().Add(cond);
				scopes.Push(cond.TrueStatements);
			} else if(ctrlFlow.jumpTypes[line] == JumpType.Else) {
				scopes.Push(((CodeConditionStatement)statFromScope[scopes.Pop()]).FalseStatements);
			} else if(statements[line] != null) {
				scopes.Peek().Add(statements[line]);
			}
		}
		return line;
	}
	public CodeMemberMethod BuildMethod(int lineBegin) {
		var entry = entryFromName[ctrlFlow.entries[lineBegin]];
		var method = entry.BuildMember();
		var scope = method.Statements;
		var lineEnd = BuildFunc(lineBegin, scope);
		if(lineEnd < ir.irCode.Length && ctrlFlow.entryContinue[lineEnd])
			scope.Add(new CodeExpressionStatement(new CodeDelegateInvokeExpression(
				entryExprs[ctrlFlow.entries[lineEnd]])));
		if(scope.Count > 0 && scope[scope.Count-1] == StatGen.Return)
			scope.RemoveAt(scope.Count-1);
		return method;
	}

	public void GenerateCode(System.IO.TextWriter writer) {
		var usedIdentifiers = new HashSet<string>();

		writer.WriteLine($"public class {name} : UdonSharp.UdonSharpBehaviour {{");

		using(var indentWriter = new IndentedTextWriter(writer, "\t")) {
			indentWriter.Indent ++;
			indentWriter.Write("\t");
			foreach(var symbol in symbols)
				if(symbol.exported) {
					usedIdentifiers.Add(symbol.name);
					ExprGen.GenerateCode(symbol.BuildMember(), indentWriter);
				}
		}

		var symbolFromVarName = new Dictionary<string, Symbol>();
		foreach(var symbol in symbols)
			symbolFromVarName[symbol.ToString()] = symbol;

		foreach(var expr in symbolFromExpr.Keys)
			expr.VariableName = "\uE000" + expr.VariableName;
		for(int line=0; line<ir.irCode.Length; line++)
			if(ctrlFlow.entries[line] != null) {
				using(var stringWriter = new System.IO.StringWriter()) {
					using(var indentWriter = new IndentedTextWriter(stringWriter, "\t")) {
						indentWriter.Indent ++;
						indentWriter.Write("\t");
						ExprGen.GenerateCode(BuildMethod(line), indentWriter);
					}
					var code = StatGen.PatchOperator(stringWriter.ToString());
					for(var m = Regex.Match(code, @"\uE000(\w+)", RegexOptions.Compiled); m.Success; m = m.NextMatch())
						if(usedIdentifiers.Add(m.Groups[1].Value)) {
							var symbol = symbolFromVarName[m.Groups[1].Value];
							using(var indentWriter = new IndentedTextWriter(writer, "\t")) {
								indentWriter.Indent ++;
								indentWriter.Write("\t");
								ExprGen.GenerateCode(symbol.BuildMember(), indentWriter);
							}
						}
					writer.Write(code.Replace("\uE000", ""));
				}
			}

		writer.WriteLine("}");
	}
}
}