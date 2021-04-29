﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DefaultDocumentation.Helper;
using DefaultDocumentation.Model;
using DefaultDocumentation.Model.Member;
using DefaultDocumentation.Model.Type;

namespace DefaultDocumentation.Writer
{
    internal abstract class DocItemWriter
    {
        private readonly Settings _settings;
        private readonly Dictionary<string, DocItem> _items;
        private readonly ConcurrentDictionary<DocItem, string> _fileNames;

        protected IEnumerable<DocItem> Items => _items.Values;

        protected DocItemWriter(Settings settings)
        {
            _settings = settings;
            _items = DocItemReader.GetItems(settings.AssemblyFile, settings.DocumentationFile, settings.AssemblyPageName, settings.ExternLinksFiles);

            _fileNames = new ConcurrentDictionary<DocItem, string>();
        }

        protected bool TryGetDocItem(string id, out DocItem item) => _items.TryGetValue(id, out item);

        protected string GetCode(string source, string region)
        {
            if (!Path.IsPathRooted(source) && _settings.ProjectDirectory != null)
            {
                source = Path.Combine(_settings.ProjectDirectory.FullName, source);
            }

            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"Unable to find code documentation file \"{source}\".");
            }

            string code = File.ReadAllText(source);
            if (!string.IsNullOrEmpty(region))
            {
                code = CodeRegion.Extract(code, region);
                if (code is null)
                {
                    throw new InvalidOperationException($"Unable to find region \"{region}\" in file \"{source}\".");
                }
            }

            // remove \r to be consistent with xml content
            return code.Replace("\r", string.Empty);
        }

        protected bool HasOwnPage(DocItem item) => item switch
        {
            AssemblyDocItem => (_settings.GeneratedPages & GeneratedPages.Assembly) != 0 || !string.IsNullOrEmpty(_settings.AssemblyPageName) || item.Documentation != null || GetChildren<NamespaceDocItem>(item).Skip(1).Any(),
            NamespaceDocItem => (_settings.GeneratedPages & GeneratedPages.Namespaces) != 0,
            ClassDocItem => (_settings.GeneratedPages & GeneratedPages.Classes) != 0,
            DelegateDocItem => (_settings.GeneratedPages & GeneratedPages.Delegates) != 0,
            EnumDocItem => (_settings.GeneratedPages & GeneratedPages.Enums) != 0,
            InterfaceDocItem => (_settings.GeneratedPages & GeneratedPages.Interfaces) != 0,
            StructDocItem => (_settings.GeneratedPages & GeneratedPages.Structs) != 0,
            ConstructorDocItem => (_settings.GeneratedPages & GeneratedPages.Constructors) != 0,
            EventDocItem => (_settings.GeneratedPages & GeneratedPages.Events) != 0,
            FieldDocItem => (_settings.GeneratedPages & GeneratedPages.Fields) != 0,
            MethodDocItem => (_settings.GeneratedPages & GeneratedPages.Methods) != 0,
            OperatorDocItem => (_settings.GeneratedPages & GeneratedPages.Operators) != 0,
            PropertyDocItem => (_settings.GeneratedPages & GeneratedPages.Properties) != 0,
            ExplicitInterfaceImplementationDocItem => (_settings.GeneratedPages & GeneratedPages.ExplicitInterfaceImplementations) != 0,
            _ => false
        };

        protected string GetFileName(DocItem item) => _fileNames.GetOrAdd(item, i => _settings.PathCleaner.Clean(i is AssemblyDocItem ? i.FullName : _settings.FileNameMode switch
        {
            FileNameMode.Md5 => Convert.ToBase64String(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(i.FullName))),
            FileNameMode.Name => i.LongName,
            _ => i.FullName
        }));

        protected IEnumerable<T> GetChildren<T>(DocItem item) where T : DocItem
        {
            IEnumerable<DocItem> GetAllChildren(DocItem item)
            {
                foreach (DocItem child in Items.Where(i => i.Parent == item))
                {
                    yield return child;
                    foreach (DocItem indirectChild in GetAllChildren(child))
                    {
                        yield return indirectChild;
                    }
                }
            }

            return (item switch
            {
                NamespaceDocItem when typeof(T).IsSubclassOf(typeof(TypeDocItem)) && (_settings.NestedTypeVisibilities & NestedTypeVisibilities.Namespace) != 0 => GetAllChildren(item),
                TypeDocItem when typeof(T).IsSubclassOf(typeof(TypeDocItem)) && (_settings.NestedTypeVisibilities & NestedTypeVisibilities.DeclaringType) == 0 => Enumerable.Empty<T>(),
                _ => Items.Where(i => i.Parent == item)
            }).OfType<T>().OrderBy(c => c.FullName);
        }

        protected abstract void Clean(DirectoryInfo directory);

        protected abstract string GetUrl(DocItem item);

        protected abstract void WritePage(DirectoryInfo directory, DocItem item);

        public void Execute()
        {
            Clean(_settings.OutputDirectory);

            foreach (DocItem item in Items.Where(HasOwnPage))
            {
                try
                {
                    WritePage(_settings.OutputDirectory, item);
                }
                catch (Exception exception)
                {
                    throw new Exception($"Error while writing documentation for {item.FullName}", exception);
                }
            }

            if (_settings.LinksOutputFile != null)
            {
                _settings.LinksOutputFile.Directory.Create();

                using StreamWriter writer = _settings.LinksOutputFile.CreateText();

                writer.WriteLine(_settings.LinksBaseUrl);
                foreach (DocItem item in Items.Where(i => i is not ExternDocItem && i is not AssemblyDocItem))
                {
                    writer.Write(item.Id);
                    writer.Write('|');
                    writer.Write(GetUrl(item));
                    writer.Write('|');
                    writer.WriteLine(item.Name);
                }
            }
        }
    }
}
