using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpTicker.SourceGenerator.Validation
{
    /// <summary>
    /// Handles validation of constructor configurations for TickerFunction classes.
    /// </summary>
    internal static class ConstructorValidator
    {
        /// <summary>
        /// Validates that a class doesn't have multiple constructors.
        /// Issues a warning if multiple constructors are found and no SharpTickerConstructor attribute is present.
        /// Issues an error if multiple constructors have SharpTickerConstructor attribute.
        /// </summary>
        public static void ValidateMultipleConstructors(
            ClassDeclarationSyntax classDeclaration,
            SemanticModel semanticModel,
            SourceProductionContext context)
        {
            var constructors = classDeclaration.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            var hasPrimaryConstructor = classDeclaration.ParameterList?.Parameters.Count > 0;
            
            // Count total constructors (regular + primary)
            var totalConstructors = constructors.Count + (hasPrimaryConstructor ? 1 : 0);
            
            // Check for SharpTickerConstructor attributes
            var constructorsWithSharpTickerAttribute = new List<ConstructorDeclarationSyntax>();
            
            foreach (var constructor in constructors)
            {
                var constructorSymbol = semanticModel.GetDeclaredSymbol(constructor);
                if (constructorSymbol != null)
                {
                    var hasSharpTickerAttribute = constructorSymbol.GetAttributes().Any(attr =>
                    {
                        var attributeClass = attr.AttributeClass;
                        if (attributeClass == null) return false;
                        
                        var attributeName = attributeClass.Name;
                        var fullName = attributeClass.ToDisplayString();
                        
                        return attributeName == "SharpTickerConstructorAttribute" || 
                               attributeName == "SharpTickerConstructor" ||
                               fullName == "SharpTicker.Utilities.SharpTickerConstructorAttribute" ||
                               fullName == "SharpTicker.Utilities.SharpTickerConstructor";
                    });
                    
                    if (hasSharpTickerAttribute)
                    {
                        constructorsWithSharpTickerAttribute.Add(constructor);
                    }
                }
            }
            
            // Error if multiple constructors have SharpTickerConstructor attribute
            if (constructorsWithSharpTickerAttribute.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MultipleSharpTickerConstructorAttributes,
                    classDeclaration.Identifier.GetLocation(),
                    classDeclaration.Identifier.Text
                ));
            }
            // Warning if multiple constructors exist but no SharpTickerConstructor attribute
            else if (totalConstructors > 1 && constructorsWithSharpTickerAttribute.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MultipleConstructors,
                    classDeclaration.Identifier.GetLocation(),
                    classDeclaration.Identifier.Text
                ));
            }
        }
    }
}
