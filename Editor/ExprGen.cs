using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;
using System.CodeDom.Compiler;
using VRC.Udon;
using VRC.Udon.Common;

namespace UdonFlat {
public class ExprGen {
	public static CodeExpression op_LogicalNot = new CodeVariableReferenceExpression("!");
	public static CodePrimitiveExpression True = new CodePrimitiveExpression(true);
	public static CodePrimitiveExpression False = new CodePrimitiveExpression(false);
	public static CodeExpression Not(CodeExpression expr) {
		var prim = expr as CodePrimitiveExpression;
		if(prim != null && prim.Value is bool)
			return new CodePrimitiveExpression(!(bool)prim.Value);
		var invoke = expr as CodeDelegateInvokeExpression;
		if(invoke != null && invoke.TargetObject == op_LogicalNot)
			return invoke.Parameters[0];
		return new CodeDelegateInvokeExpression(op_LogicalNot, expr); 
	}
	
	static Dictionary<System.Type, int> vectorTypeSizes = new Dictionary<System.Type, int>(){
		{typeof(Vector2), 2}, {typeof(Vector2Int), 2},
		{typeof(Vector3), 3}, {typeof(Vector3Int), 3},
		{typeof(Vector4), 4}, {typeof(Quaternion), 4},
		{typeof(Color32), 4}, {typeof(Color), 4}, 
	};
	public static CodeExpression Ref(object value) {
		var type = value?.GetType();
		if(value == null || type.IsPrimitive || value is string || value is decimal)
			return new CodePrimitiveExpression(value);
		if(type.IsValueType) {
			if(type.IsEnum) {
				var name = System.Enum.GetName(type, value);
				if(name == null)
					return new CodeCastExpression(Type(type), new CodePrimitiveExpression((int)value));
				return new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(Type(type)), name);
			}
			if(vectorTypeSizes.TryGetValue(type, out var size)) {
				var indexer = type.GetProperty("Item");
				var expr = new CodeObjectCreateExpression(Type(type));
				for(int i=0; i<size; i++)
					expr.Parameters.Add(new CodePrimitiveExpression(indexer.GetValue(value, new object[]{i})));
				return expr;
			}
			// TODO: Bounds, Rect, RectInt, LayerMask
		} else {
			switch(value) {
			case System.Type t:
				return new CodeTypeOfExpression(Type(t));
			case UdonGameObjectComponentHeapReference heapRef:
				switch(heapRef.type.Name) {
				case nameof(GameObject):
					return new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "gameObject");
				case nameof(Transform):
					return new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "transform");
				default: // UdonBehaviour, Object
					return new CodeThisReferenceExpression();
				}
			}
		}
		return null;
	}
	static bool IsArrayZero(System.Array array) {
		var elemType = array.GetType().GetElementType();
		var elemDef = elemType.IsValueType ? System.Activator.CreateInstance(elemType) : null;
		var n = array.GetLength(0);
		for(int i=0; i<n; i++)
			if(!object.Equals(array.GetValue(i), elemDef))
				return false;
		return true;
	}
	public static CodeExpression Value(object value) {
		var refValue = Ref(value);
		if(refValue != null)
			return refValue;
		var type = value.GetType();
		if(type.IsArray) {
			var array = (System.Array)value;
			var expr = new CodeArrayCreateExpression(Type(type), array.GetLength(0));
			if(!IsArrayZero(array))
				for(int i=0; i<expr.Size; i++)
					expr.Initializers.Add(Value(array.GetValue(i)));
			return expr;
		}
		switch(value) {
		case VRC.SDKBase.VRCUrl url:
			return new CodeObjectCreateExpression(Type(type), new CodePrimitiveExpression(url.Get()));
		// TODO: AnimationCurve, Gradient
		}
		return new CodeSnippetExpression($"???{value}???");
	}

	public static CodeTypeReference Type(System.Type type) {
		// TODO: namespace stripper
		return new CodeTypeReference(type);
	}

	public static void GenerateCode(CodeObject obj, System.IO.TextWriter writer) {
		using(var provider = CodeDomProvider.CreateProvider("CSharp")) {
			var options = new CodeGeneratorOptions();
			options.IndentString = "\t";
			options.BlankLinesBetweenMembers = false;
			options.ElseOnClosing = true;
			switch(obj) {
			case CodeExpression expr:
				provider.GenerateCodeFromExpression(expr, writer, options); return;
			case CodeStatement stat:
				provider.GenerateCodeFromStatement(stat, writer, options); return;
			case CodeTypeMember member:
				provider.GenerateCodeFromMember(member, writer, options); return;
			}
		}
	}
	public static string GenerateCode(CodeObject obj) {
		using(var writer = new System.IO.StringWriter()) {
			GenerateCode(obj, writer);
			return writer.ToString();
		}
	}
}
}