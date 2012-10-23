﻿using System;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace SharpRepository.CouchDbRepository.Linq
{
    public class CouchDbExpressionVisitor : ExpressionVisitor
    {
        private bool _isCount;
        private string _filter = string.Empty;
        private string _orderBy;
        private int? _skip;
        private int? _take;
        private bool _isFirst = false;
        private bool _isDescending = false;


        private string _result = String.Empty;

        public void Parse(Expression expression, string docType, out string postData, out string querystring)
        {
            postData = null;
            querystring = null;

            Visit(expression);

            if (String.IsNullOrEmpty(_result))
                return;

            if (_take.HasValue)
                querystring += "limit=" + _take.Value + "&";
            else if (_isFirst)
                querystring += "limit=1&";

            if (_isDescending)
                querystring += "descending=true&";

            if (String.IsNullOrEmpty(_orderBy))
                _orderBy = "_id";

            postData = "{ \"map\":\"function (doc) {if (" + _result + ") emit(doc." + _orderBy + ", doc);}\"}";

        }

        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "First":
                case "FirstOrDefault":
                    _isFirst = true;
                    break;

                case "OrderBy":
                case "OrderByDescending":
                case "ThenBy": // at this point these won't work, they will be ignored if sort by is set, maybe there is away to sort by multiple using an array as the key but not sure
                case "ThenByDescending":
                    SetOrderBy(m, m.Method.Name.Contains("Descending"));
                    break;

                case "Count":
                    _isCount = true;
                    break;

                case "GroupBy":
                case "StartsWith":
                case "Contains":
                case "Length":
                case "ToUpper":
                case "ToLower":
                case "Where":
                    SetWhereQuery(m);
                    break;

                case "Skip":
                    SetSkipQuery(m);
                    break;

                case "Take":
                    SetTakeQuery(m);
                    break;

                case "Max":
                case "Min":
                    throw new NotSupportedException();

                default:
                    throw new NotSupportedException();
            }

            return base.VisitMethodCall(m);
        }

        private void SetSkipQuery(MethodCallExpression m)
        {
            if (m.Arguments.Count == 1)
                return;

            var arg = m.Arguments[1];

            if (arg.NodeType == ExpressionType.Constant)
            {
                _skip = (int) ((ConstantExpression) arg).Value;
            }

            // TODO: handle variable expression
        }

        private void SetTakeQuery(MethodCallExpression m)
        {
            if (m.Arguments.Count == 1)
                return;

            var arg = m.Arguments[1];

            if (arg.NodeType == ExpressionType.Constant)
            {
                _take = (int)((ConstantExpression)arg).Value;
            }

            // TODO: handle variable expression
        }

        private void SetOrderBy(MethodCallExpression m, bool isDescending)
        {
            if (!String.IsNullOrEmpty(_orderBy))
                return; // ignore calls to ThenBy and ThenByDescending at this point

            if (m.Arguments.Count == 1)
                return;

            var arg = m.Arguments[1];

            var unExpr = arg as UnaryExpression;
            var op = unExpr.Operand as LambdaExpression;
            var prop = op.Body as MemberExpression;

            _orderBy = prop.Member.Name;
            _isDescending = isDescending;
        }

        private void SetWhereQuery(MethodCallExpression m)
        {
            if (m.Arguments.Count == 1)
                return;

            Expression arg = m.Arguments[1];
            var unExpr = arg as UnaryExpression;
            var op = unExpr.Operand as LambdaExpression;

            // not sure if this will end up working, but trying to parse the string representation of the predicate and just manipulate with replace and regex replaces to get the JS syntax

            _result = op.ToString()
               
                // Or syntax
                .Replace(" OrElse ", " || ")
               .Replace(" Or ", " || ")

               // And syntax
               .Replace(" AndAlso ", " && ")
               .Replace(" And ", " && ")

               // change quotes from " to '
               .Replace("\"", "'")

               // changing case methods
               .Replace(".ToUpper(", ".toUpperCase(")
               .Replace(".ToLower(", ".toLowerCase(")
               ;
               
            // StartsWith
            _result = Regex.Replace(_result, @"\.StartsWith\('([A-Za-z0-9_]*)'\)", ".indexOf('$1') == 0");

            // EndsWith
            _result = Regex.Replace(_result, @"p\.([A-Za-z0-9_]*)\.EndsWith\('([A-Za-z0-9_]*)'\)", "p.$1.indexOf('$2', p.$1.length - '$2'.length) != -1");

            // Contains
            _result = Regex.Replace(_result, @"\.Contains\('([A-Za-z0-9_]*)'\)", ".indexOf('$1') != -1");

            // Length
            _result = Regex.Replace(_result, @"p\.([A-Za-z0-9_]*)\.Length", "p.$1.length");


            _result = _result
                .Replace("p.", "doc.")
                .Replace("p => ", "")
                ;

            //AppendFilter(CreateOperand(op.Body));
        }

        private void AppendFilter(string param)
        {
            if (string.IsNullOrWhiteSpace(_result))
            {
                _result = param;
            }
        }

        private string CreateOperand(Expression expression)
        {
            var binary = expression as BinaryExpression;

            if (binary != null)
            {
                return CreateBinaryOperand(binary);
            }

            var methodCall = expression as MethodCallExpression;

            if (methodCall != null)
            {
                return CreateMethodOperand(methodCall);
            }

            throw new NotImplementedException();
        }

        private string CreateBinaryOperand(BinaryExpression body)
        {
            var left = body.Left as MemberExpression;
            var right = body.Right;
            var leftPart = "doc." + left.Member.Name;

            if (left.Expression.NodeType == ExpressionType.MemberAccess)
            {
                switch (leftPart)
                {
                    case "Length":
                        leftPart = String.Format("doc.{0}.length", ((MemberExpression)left.Expression).Member.Name);
                        break;
                    case "ToUpper": // NodeType above is Call not MemberExpression
                        leftPart = String.Format("doc.{0}.toUpperCase()", ((MemberExpression)left.Expression).Member.Name);
                        break;
                    case "ToLower":
                        leftPart = String.Format("doc.{0}.toLowerCase()", ((MemberExpression)left.Expression).Member.Name);
                        break;
                }
            }

            switch (body.NodeType)
            {
                case ExpressionType.Equal:
                    return string.Format("{0} == '{1}'", leftPart, GetValue(right));

                case ExpressionType.NotEqual:
                    return string.Format("{0} != '{1}'", leftPart, GetValue(right));

                case ExpressionType.GreaterThan:
                    return string.Format("{0} > '{1}'", leftPart, GetValue(right));

                case ExpressionType.GreaterThanOrEqual:
                    return string.Format("{0} >= '{1}'", leftPart, GetValue(right));

                case ExpressionType.LessThan:
                    return string.Format("{0} < '{1}'", leftPart, GetValue(right));

                case ExpressionType.LessThanOrEqual:
                    return string.Format("{0} <= '{1}'", leftPart, GetValue(right));

                case ExpressionType.And:
                case ExpressionType.AndAlso:
                    return string.Format("{0} && {1}", leftPart, GetValue(right));

                case ExpressionType.Or:
                case ExpressionType.OrElse:
                    return string.Format("{0} || {1}", leftPart, GetValue(right));

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        //public string DynamicUnary(ExpressionType nodeType, MemberExpression left, Expression right)
        //{
            
        //}

        private static string CreateMethodOperand(MethodCallExpression methodCall)
        {
            switch (methodCall.Method.Name)
            {
                case "StartsWith":
                    return string.Format("doc.{0}.indexOf('{1}') == 0", ((MemberExpression)methodCall.Object).Member.Name, GetValue(methodCall.Arguments[0]));

                case "EndsWith":
                    return string.Format("doc.{0}.indexOf('{1}', {0}.length - '{1}'.length) != -1", ((MemberExpression)methodCall.Object).Member.Name, GetValue(methodCall.Arguments[0]));

                case "Contains":
                    return string.Format("doc.{0}.indexOf('{1}') != -1", ((MemberExpression)methodCall.Object).Member.Name, GetValue(methodCall.Arguments[0]));

                default:
                    throw new ArgumentOutOfRangeException();
            }

        }

        private static object GetValue(Expression memberExpression)
        {
            return Expression.Lambda(memberExpression).Compile().DynamicInvoke();
        }

        //private string DynamicUnary(string name, MemberExpression left, Expression right)
        //{
        //    return string.Format("{0}({1},'{2}')", name, left.Member.Name, GetValue(right));
        //}
    }
}