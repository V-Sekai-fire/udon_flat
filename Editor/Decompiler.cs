using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Graph;
using VRC.Udon.Editor;

namespace SharperUdon {
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

		prettyName = unmangleName(name, addr);

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
		var field = new CodeMemberField(type, ToString());
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
				new CodeAttributeArgument(new CodePropertyReferenceExpression(
					new CodeTypeReferenceExpression("UdonSharp.UdonSyncMode"),
					syncInterp.Value.ToString()))));
		}
		return field;
	}
	static string unmangleName(string name, uint addr) {
		var m = Regex.Match(name, @"^__\d+_(.+)_\w+$");
		if(m.Success)
			return $"{m.Groups[1].Value}_{addr}_";
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
	DataFlowAnalyzer.EventEntry eventEntry;

	public Entry(string name, IUdonSymbolTable table, DataFlowAnalyzer.EventEntry eventEntry) {
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
					eventEntry.nodeDef.parameters[i].type, eventEntry.symbolNames[i]));
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
	public CtrlFlowAnalyzer ctrlFlow;
	public DataFlowAnalyzer dataFlow;

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
				symbolTable, heap, syncTable, !dataFlow.variables.Contains(symbolNames[i]));
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

		ctrlFlow = new CtrlFlowAnalyzer{program=program, ir=ir};
		ctrlFlow.Analyze();

		dataFlow = new DataFlowAnalyzer{program=program, ir=ir, ctrlFlow=ctrlFlow};
		dataFlow.Analyze();

		InitSymbols();
	}
	
	int getAssignLeftUsageCount(int line, CodeExpression assignLeft) {
		var assignVar = assignLeft as CodeVariableReferenceExpression;
		if(assignVar == null)
			return 2;
		var linePorts = dataFlow.ports[line];
		return linePorts[linePorts.Length-1].outputRef;
	}

	void SimplifyInParameters(int line, CodeExpression[] paramExprs) {
		// return;
		for(int i=0; i<paramExprs.Length; i++) {
			var refExpr = paramExprs[i] as CodeVariableReferenceExpression;
			if(refExpr == null)
				continue;
			var symbol = symbolFromExpr[refExpr];
			if(symbol.constExpr != null)
				paramExprs[i] = symbol.constExpr;
		}
		
		for(int prevLine=line-1; prevLine>=0 && !ctrlFlow.jumpTargets.Contains(prevLine+1); prevLine--) {
			if(statements[prevLine] != null) {
				var prevAssign = statements[prevLine] as CodeAssignStatement;
				if(prevAssign == null)
					break;

				// Debug.Log($"{line} try merge {ExprGen.GenerateCode(prevAssign)} into {string.Join(", ", paramExprs.Select(x=>$"{x}"))}");
				if(getAssignLeftUsageCount(prevLine, prevAssign.Left) > 1) {
					// Debug.Log("getAssignLeftUsageCount fail");
					break;
				}
				var idx = System.Array.IndexOf(paramExprs, prevAssign.Left);
				if(idx < 0) {
					// Debug.Log("IndexOf fail");
					break;
				}
				// Debug.Log("OK");

				paramExprs[idx] = prevAssign.Right;
				statements[prevLine] = null;
			}
		}
	}
	void SimplifyStatement(int line) {
		// return;
		var assign = statements[line] as CodeAssignStatement;
		if(assign != null) {
			var indexer = assign.Right as CodeArrayIndexerExpression;
			if(indexer != null && indexer.TargetObject is CodeVariableReferenceExpression) {
				var symbol = symbolFromExpr[(CodeVariableReferenceExpression)indexer.TargetObject];
				if(symbol.caster != null && indexer.Indices.Count == 1)
					 assign.Right = new CodeCastExpression(symbol.caster, indexer.Indices[0]);
			}
			if(getAssignLeftUsageCount(line, assign.Left) == 0) {
				statements[line] = new CodeExpressionStatement(assign.Right);
			}
		}
	}

	public Dictionary<CodeVariableReferenceExpression, Symbol> symbolFromExpr;
	public CodeStatement[] statements;
	Dictionary<string, CodeVariableReferenceExpression> entryExprs;

	public void Translate() {
		var symbolExprs = new Dictionary<string, CodeVariableReferenceExpression>();
		symbolFromExpr = new Dictionary<CodeVariableReferenceExpression, Symbol>();
		foreach(var symbol in symbols) {
			symbolExprs[symbol.name] = new CodeVariableReferenceExpression($"var_{symbol.name}");
			symbolFromExpr[symbolExprs[symbol.name]] = symbol;
		}

		entryExprs = new Dictionary<string, CodeVariableReferenceExpression>();
		foreach(var entry in entries)
			entryExprs[entry.name] = new CodeVariableReferenceExpression(entry.ToString());

		statements = new CodeStatement[ir.irCode.Length];
		for(int line=0; line<ir.irCode.Length; line++) {
			var instr = ir.irCode[line];
			if(instr.opcode == Opcode.EXTERN) {
				if(dataFlow.port0[line].outputRef == 0)
					continue;
				var nodeDef = GetNodeDefinition(instr.arg0);
				var paramExprs = instr.args.Select((s,i) =>
					nodeDef.parameters[i].parameterType == UdonNodeParameter.ParameterType.IN ?
						(CodeExpression)symbolExprs[s] : null).ToArray();
				SimplifyInParameters(line, paramExprs);
				for(int i=0; i<paramExprs.Length; i++)
					if(paramExprs[i] == null)
						paramExprs[i] = symbolExprs[instr.args[i]];
				statements[line] = StatGen.Extern(nodeDef, paramExprs);
				// COPY may produce casting
				if(instr.arg0 == StatGen.COPY) {
					var assign = (CodeAssignStatement)statements[line];
					var sourceType = symbolFromName[instr.args[0]].type;
					var targetType = symbolFromName[instr.args[1]].type;
					if(!targetType.IsAssignableFrom(sourceType))
						assign.Right = new CodeCastExpression(targetType, assign.Right);
				}
				SimplifyStatement(line);

			} else if(instr.opcode == Opcode.CALL) {
				Debug.Assert(instr.args == null);
				statements[line] = new CodeExpressionStatement(new CodeDelegateInvokeExpression(
					entryExprs[ctrlFlow.entries[ir.irLineFromAddr[instr.arg0]]]));
			} else if(instr.opcode == Opcode.JUMP || instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN) {
				var paramExprs = new CodeExpression[]{new CodePrimitiveExpression(true)};
				if(instr.args != null) {
					paramExprs[0] = symbolExprs[instr.args[0]];
					SimplifyInParameters(line, paramExprs);
					paramExprs[0] = ExprGen.Not(paramExprs[0]);
				}
				if(ctrlFlow.jumpTypes[line] == JumpType.Loop)
					statements[line] = StatGen.If(ExprGen.Not(paramExprs[0]), StatGen.Break);
				else if(ctrlFlow.jumpTypes[line] == JumpType.If)
					statements[line] = new CodeConditionStatement(ExprGen.Not(paramExprs[0]));
				else if(ctrlFlow.jumpTypes[line] == JumpType.Else)
					statements[line] = new CodeConditionStatement(); // stub
				else {
					statements[line] = StatGen.If(paramExprs[0],
						ctrlFlow.jumpTypes[line] == JumpType.Break ? 
							StatGen.Break :
						ctrlFlow.jumpTypes[line] == JumpType.Continue ?
							StatGen.Continue :
						instr.opcode == Opcode.EXIT || instr.opcode == Opcode.RETURN ?
							(CodeStatement) new CodeMethodReturnStatement() :
							new CodeGotoStatement(ctrlFlow.labels[ir.irLineFromAddr[instr.arg0]]));
				}
			} else {
				Debug.LogError(instr);
			}
		}
	}
	public void CreateBlocks(int line, CodeStatementCollection scope0) {
		var statFromScope = new Dictionary<CodeStatementCollection, CodeStatement>();
		var scopes = new Stack<CodeStatementCollection>(new[]{scope0});
		for(; line<ir.irCode.Length; line++) {
			for(int i=ctrlFlow.choiceEnd[line]; i>0; i--) {
				var cond = (CodeConditionStatement)statFromScope[scopes.Pop()];
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
							var scope = scopes.Peek();
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
				var iter = (CodeIterationStatement)statFromScope[scopes.Pop()];
				if(iter.Statements.Count > 0) {
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
						var scope = scopes.Peek();
						if(scope.Count > 1) {
							var assign = scope[scope.Count-2] as CodeAssignStatement;
							if(assign?.Left == iterVar) {
								scope.RemoveAt(scope.Count-2);
								iter.InitStatement = assign;
							}
						}
					}
				}
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
			if(line+1 < ir.irCode.Length && ctrlFlow.entries[line+1] != null) {
				if(ctrlFlow.entryContinue[line+1])
					scopes.Peek().Add(new CodeExpressionStatement(new CodeDelegateInvokeExpression(
						entryExprs[ctrlFlow.entries[line+1]])));
				break;
			}
		}
	}
	public CodeMemberMethod BuildMethod(int line) {
		Debug.Assert(ctrlFlow.entries[line] != null);
		var entry = entryFromName[ctrlFlow.entries[line]];
		var method = entry.BuildMember();
		CreateBlocks(line, method.Statements);
		return method;
	}

	public void GenerateCode(System.IO.TextWriter writer) {
		var usedSymbols = new HashSet<Symbol>();

		writer.WriteLine($"public class {name} : UdonSharp.UdonSharpBehaviour {{");

		var methodCode = new List<string>();
		for(int line=0; line<ir.irCode.Length; line++)
			if(ctrlFlow.entries[line] != null) {
				var code = StatGen.PatchOperator(ExprGen.GenerateCode(BuildMethod(line)));
				methodCode.Add(Regex.Replace(code, @"var_(\w+)", m => {
					var symbol = symbolFromName[m.Groups[1].Value];
					usedSymbols.Add(symbol);
					return symbol.ToString();
				}));
			}
		
		foreach(var symbol in symbols)
			if(symbol.exported || usedSymbols.Contains(symbol))
				writer.Write(ExprGen.GenerateCode(symbol.BuildMember()));
		foreach(var code in methodCode)
			writer.Write(code);

		writer.WriteLine("}");
	}
}
}