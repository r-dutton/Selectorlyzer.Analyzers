using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.Qulaly
{
    public static class QulalySelectorExtensions
    {
        public static IReadOnlyList<SyntaxKind> GetTopLevelSyntaxKinds(this QulalySelector selector)
        {
            if (selector is null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            return EnumerableMatcher.GetTopLevelSyntaxKinds(selector);
        }
    }
}
