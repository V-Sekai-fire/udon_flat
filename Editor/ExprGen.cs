using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using System.CodeDom.Compiler;
using VRC.Udon;
using VRC.Udon.Editor;
using VRC.Udon.Graph;
using VRC.Udon.Common;

namespace SharperUdon {
public class ExprGen {
	public static CodeExpression Negate(CodeExpression expr) {
		var prim = expr as CodePrimitiveExpression;
		if(prim != null && prim.Value is bool)
			return new CodePrimitiveExpression(!(bool)prim.Value);
		var invoke = expr as CodeDelegateInvokeExpression;
		if(invoke != null && invoke.TargetObject == opUnary["op_UnaryNegation"])
			return invoke.Parameters[0];
		return new CodeDelegateInvokeExpression(opUnary["op_UnaryNegation"], expr); 
	}
	static Dictionary<string, CodeExpression> opUnary = new Dictionary<string, CodeExpression>{
		{"op_UnaryPlus", new CodeSnippetExpression("+")},
		{"op_UnaryMinus", new CodeSnippetExpression("-")},
		{"op_UnaryNegation", new CodeSnippetExpression("!")},
	};
	static Dictionary<string, CodeBinaryOperatorType> opBinary = new Dictionary<string, CodeBinaryOperatorType>{
		{"op_Addition", CodeBinaryOperatorType.Add},
		{"op_Subtraction", CodeBinaryOperatorType.Subtract},
		{"op_Multiply", CodeBinaryOperatorType.Multiply},
		{"op_Multiplication", CodeBinaryOperatorType.Multiply},
		{"op_Division", CodeBinaryOperatorType.Divide},
		{"op_Modulus", CodeBinaryOperatorType.Modulus},
		{"op_Remainder", CodeBinaryOperatorType.Modulus},
		
		{"op_Equality", CodeBinaryOperatorType.IdentityEquality},
		{"op_Inequality", CodeBinaryOperatorType.IdentityInequality},
		{"op_LessThan", CodeBinaryOperatorType.LessThan},
		{"op_LessThanOrEqual", CodeBinaryOperatorType.LessThanOrEqual},
		{"op_GreaterThan", CodeBinaryOperatorType.GreaterThan},
		{"op_GreaterThanOrEqual", CodeBinaryOperatorType.GreaterThanOrEqual},

		{"op_ConditionalAnd", CodeBinaryOperatorType.BooleanAnd},
		{"op_ConditionalOr", CodeBinaryOperatorType.BooleanOr},
		{"op_LogicalAnd", CodeBinaryOperatorType.BitwiseAnd},
		{"op_LogicalOr", CodeBinaryOperatorType.BitwiseOr},
		// missing: shift, xor
	};
	static Dictionary<UdonNodeParameter.ParameterType, FieldDirection> dirParameter = new Dictionary<UdonNodeParameter.ParameterType, FieldDirection> {
		{UdonNodeParameter.ParameterType.IN, FieldDirection.In},
		{UdonNodeParameter.ParameterType.OUT, FieldDirection.Out},
		{UdonNodeParameter.ParameterType.IN_OUT, FieldDirection.Ref},
	};
	public static string COPY = "Get_Variable";
	public static CodeStatement Extern(UdonNodeDefinition nodeDef, CodeExpression[] paramExprs=null) {
		var name = nodeDef.name.Split(' ').Last();
		var type = nodeDef.type;
		var typeExpr = (CodeExpression)new CodeTypeReferenceExpression(type);

		var paramDefs = nodeDef.parameters;
		if(paramExprs == null)
			paramExprs = Enumerable.Range(0, paramDefs.Count).Select(
				i => (CodeExpression)new CodeVariableReferenceExpression($"_{i}")).ToArray();

		// TODO: generic
		var rhs = (CodeExpression)null;
		var lhs = (CodeExpression)null;
		if(nodeDef.fullName == COPY) {
			lhs = paramExprs[1];
			rhs = paramExprs[0];
		}
		if(rhs == null) { // operator & constructor
			if(opBinary.ContainsKey(name) && paramDefs.Count == 3)
				rhs = new CodeBinaryOperatorExpression(paramExprs[0], opBinary[name], paramExprs[1]);
			else if(opUnary.ContainsKey(name) && paramDefs.Count == 2)
				rhs = new CodeDelegateInvokeExpression(opUnary[name], paramExprs[0]);
			else if((name == "op_Implicit" || name == "op_Explicit") && paramDefs.Count == 2)
				rhs = new CodeCastExpression(paramDefs[1].type, paramExprs[0]);
			else if(name == "ctor") {
				if(type.IsArray && paramDefs.Count == 2)
					rhs = new CodeArrayCreateExpression(type, paramExprs[0]);
				else 
					rhs = new CodeObjectCreateExpression(type, paramExprs.Take(paramDefs.Count-1).ToArray());
			}
			if(rhs != null)
				lhs = paramExprs[paramDefs.Count-1];
		}
		if(rhs == null) { // getter & setter
			if(Regex.IsMatch(name, type.IsArray ? "^(Get|Set)$" : "^(get_Item|set_Item)$") && paramDefs.Count >= 2) {
				var hasInstance = paramDefs[0].type == type;
				var targetExpr = hasInstance ? paramExprs[0] : typeExpr;
				var indexExprs = paramExprs.Take(paramDefs.Count-1).Skip(hasInstance?1:0).ToArray();
				rhs = type.IsArray ? new CodeArrayIndexerExpression(targetExpr, indexExprs) :
					(CodeExpression) new CodeIndexerExpression(targetExpr, indexExprs);
			} else if(Regex.IsMatch(name, "^(get_|set_)") && (paramDefs.Count == 1 || paramDefs.Count == 2)) {
				var targetExpr = paramDefs.Count-2 >= 0 ? paramExprs[paramDefs.Count-2] : typeExpr;
				rhs = new CodePropertyReferenceExpression(targetExpr, name.Substring(4));
			}
			if(rhs != null) {
				lhs = paramExprs[paramDefs.Count-1];
				if(Regex.IsMatch(name, "^set", RegexOptions.IgnoreCase))
					(lhs, rhs) = (rhs, lhs);
			}
		}
		if(rhs == null) { // method
			if(Regex.IsMatch(name, "^(get_|set_|op_|(ctor$))"))
				Debug.LogWarning($"missing {nodeDef.fullName}");

			var hasInstance = false;
			var hasReturn = false;
			if(paramDefs.Count > 0) {
				var first = paramDefs[0];
				var last = paramDefs[paramDefs.Count-1];
				hasInstance = first.name == "instance" && first.parameterType == UdonNodeParameter.ParameterType.IN;
				hasReturn = last.name == null && last.parameterType == UdonNodeParameter.ParameterType.OUT;
			}
			var targetExpr = hasInstance ? paramExprs[0] : typeExpr;
			var argExprs = paramExprs
				.Select((p,i) => new CodeDirectionExpression(dirParameter[paramDefs[i].parameterType], p))
				.Take(paramDefs.Count - (hasReturn?1:0)).Skip(hasInstance?1:0).ToArray();
			lhs = hasReturn ? (CodeExpression)paramExprs[paramExprs.Length-1] : null;
			rhs = (CodeExpression)new CodeMethodInvokeExpression(targetExpr, name, argExprs);
		}

		return lhs == null ? (CodeStatement)new CodeExpressionStatement(rhs) : new CodeAssignStatement(lhs, rhs);
	}
	public static CodeExpression Value(object value) {
		var type = value?.GetType();
		if(value == null || type.IsPrimitive || value is string)
			return new CodePrimitiveExpression(value);
		if(value is System.Type)
			return new CodeTypeOfExpression(value as System.Type);
		if(type.IsArray) {
			var elemType = type.GetElementType();
			var elemDef = elemType.IsValueType ? System.Activator.CreateInstance(elemType) : null;
			var arr = (System.Array)value;
			var n = arr.GetLength(0);
			for(int i=0; i<n; i++)
				if(!arr.GetValue(i).Equals(elemDef)) {
					var exprs = new CodeExpression[n];
					for(int j=0; j<n; j++)
						exprs[j] = Value(arr.GetValue(j));
					return new CodeArrayCreateExpression(type, exprs);
				}
			return new CodeArrayCreateExpression(type, n);
		}
		if(!type.IsValueType) {
			if(value is UdonGameObjectComponentHeapReference) {
				var refType = (value as UdonGameObjectComponentHeapReference).type;
				var thisExpr = new CodeThisReferenceExpression();
				if(refType == typeof(UdonBehaviour))
					return thisExpr;
				else if(refType == typeof(GameObject))
					return new CodePropertyReferenceExpression(thisExpr, "gameObject");
				else if(refType == typeof(Transform))
					return new CodePropertyReferenceExpression(thisExpr, "transform");
			}
		} else {
			
			if(type.IsEnum) {
				var typeExpr = new CodeTypeReferenceExpression(type);
				var name = System.Enum.GetName(type, value);
				if(name == null)
					return new CodeCastExpression(type, new CodePrimitiveExpression((int)value));
				else
					return new CodePropertyReferenceExpression(typeExpr, name);
			}
			if(value is Vector3) {
				var v = (Vector3)value;
				return new CodeObjectCreateExpression(type, Value(v[0]), Value(v[1]), Value(v[2]));
			}
			if(value is Vector4) {
				var v = (Vector4)value;
				return new CodeObjectCreateExpression(type, Value(v[0]), Value(v[1]), Value(v[2]), Value(v[3]));
			}
			if(value is Quaternion) {
				var v = (Quaternion)value;
				return new CodeObjectCreateExpression(type, Value(v[0]), Value(v[1]), Value(v[2]), Value(v[3]));
			}
		}
		return new CodeSnippetExpression($"???{value}???");
	}
	public static string GenerateCode(CodeObject obj) {
		using (var writer = new System.IO.StringWriter()) {
			using (var provider = CodeDomProvider.CreateProvider("CSharp")) {
				var options = new CodeGeneratorOptions();
				// options.BracingStyle = "Block";
				options.IndentString = "\t";
				options.BlankLinesBetweenMembers = false;
				if(obj is CodeExpression)
					provider.GenerateCodeFromExpression(obj as CodeExpression, writer, options);
				else if(obj is CodeStatement)
					provider.GenerateCodeFromStatement(obj as CodeStatement, writer, options);
				else if(obj is CodeTypeMember)
					provider.GenerateCodeFromMember(obj as CodeTypeMember, writer, options);
				return writer.ToString();
			}
		}
	}
	[MenuItem("AssetSaver/TestExprGen")]
	static void TestExprGen() {
		// foreach(var v in System.Enum.GetValues(typeof(UdonNodeParameter.ParameterType)))
		// 	Debug.Log($"{v}");
		var nodeDefs = UdonEditorManager.Instance.GetNodeDefinitions();
		foreach(var nodeDef in nodeDefs) {
			if(nodeDef.fullName.StartsWith(COPY)) {
				Debug.Log(nodeDef.fullName);
				Debug.Log(GenerateCode(Extern(nodeDef)));
			} 
			// if(!nodeDef.fullName.Contains(".") && !Regex.IsMatch(nodeDef.fullName, "^(Variable|Const|Type|Event)_")) {
			// 	Debug.Log(nodeDef.fullName);
			// 	Debug.Log(string.Join(", ", nodeDef.parameters.Select(p => $"({p.name}:{p.type}:{p.parameterType})")));
			// }
			// 	TranslateExtern(nodeDef);
		}
			// if(nodeDef.fullName.StartsWith("UnityEngineMeshRenderer.")) {
			// 	Debug.Log(nodeDef.fullName);
			// 	Debug.Log(GenerateCode(TranslateExtern(nodeDef)));
			// }
	}
}
}