using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.CodeDom;
using System.CodeDom.Compiler;
using VRC.Udon;
using VRC.Udon.Common;

namespace SharperUdon {
public class ExprGen {
	public static CodeExpression opNegation = new CodeSnippetExpression("!");
	public static CodeExpression Negate(CodeExpression expr) {
		var prim = expr as CodePrimitiveExpression;
		if(prim != null && prim.Value is bool)
			return new CodePrimitiveExpression(!(bool)prim.Value);
		var invoke = expr as CodeDelegateInvokeExpression;
		if(invoke != null && invoke.TargetObject == opNegation)
			return invoke.Parameters[0];
		return new CodeDelegateInvokeExpression(opNegation, expr); 
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
}
}