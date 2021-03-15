using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.CodeDom;

namespace UdonFlat {
public class StatOpt {
	public static void RewriteCond(CodeConditionStatement cond, CodeStatementCollection scope) {
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
	public static void RewriteIter(CodeIterationStatement iter, CodeStatementCollection scope) {
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
}
}