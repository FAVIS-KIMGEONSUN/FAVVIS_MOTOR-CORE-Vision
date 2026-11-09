using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FAVIS
{
    public enum ProcessPathGroup
    {
        Mask,
        Hole,
        Global,
        Local
    }

    public sealed class ProcessPathContext
    {
        public string ModelName { get; set; }
        public int? ModelNo { get; set; }
        public int? StepNo { get; set; }
        public string StepName { get; set; }
        public string WorkDir { get; set; }
    }

    public sealed class ResolvedProcessPath
    {
        public ProcessPathGroup Group { get; }
        public int Index { get; }
        public string Path { get; }

        internal ResolvedProcessPath(string path)
        {
            Path = path;
            Index = 0;
        }

        internal ResolvedProcessPath(ProcessPathGroup group, int index, string path)
        {
            Group = group;
            Index = index;
            Path = path;
        }
    }


    /// <summary>
    /// 직렬화된 Mask, Global, Local 경로 설정을 토큰 치환하여 절대 경로 목록으로 해석합니다.
    /// </summary>
    /// <remarks>
    /// 설정 문자열은 <c>Mask경로;Mask경로|Global경로;Global경로|Local경로;Local경로</c> 형식입니다.
    /// 그룹은 <c>|</c>, 그룹 내 경로는 <c>;</c>로 구분하며 생략된 그룹은 빈 목록이 됩니다.
    /// 상대 경로는 <see cref="ProcessPathContext.WorkDir"/>를 기준으로 해석됩니다.
    /// 지원 토큰은 <c>{ModelDir}</c>, <c>{ModelName}</c>, <c>{ModelNo}</c>,
    /// <c>{StepNo}</c>, <c>{StepName}</c>, <c>{WorkDir}</c>, <c>{Default}</c>입니다.
    /// <c>{Default}</c>는 <c>ModelDir\ModelNo\StepName</c> 경로로 해석됩니다.
    /// <para>사용 예:</para>
    /// <code>
    /// var context = new ProcessPathContext
    /// {
    ///     WorkDir = AppDomain.CurrentDomain.BaseDirectory,
    ///     ModelName = "Sample",
    ///     ModelNo = 1,
    ///     StepNo = 2,
    ///     StepName = "Inspect"
    /// };
    /// var resolver = new ProcessPathResolver(
    ///     @"{ModelDir}\Mask|{Default}\Global.dat|{WorkDir}\Local.dat", context);
    /// string maskPath = resolver.Mask[0].Path;
    /// string globalPath = resolver[ProcessPathGroup.Global][0].Path;
    /// </code>
    /// </remarks>
    public sealed class ProcessPathResolver
    {
        private static readonly Regex TokenPattern =
            new Regex(@"\{(?<name>[^{}]+)\}", RegexOptions.Compiled);

        private readonly ReadOnlyCollection<ResolvedProcessPath> _mask;
        private readonly ReadOnlyCollection<ResolvedProcessPath> _hole;
        private readonly ReadOnlyCollection<ResolvedProcessPath> _global;
        private readonly ReadOnlyCollection<ResolvedProcessPath> _local;

        public IReadOnlyList<ResolvedProcessPath> Mask => _mask;
        public IReadOnlyList<ResolvedProcessPath> Hole => _hole;
        public IReadOnlyList<ResolvedProcessPath> Global => _global;
        public IReadOnlyList<ResolvedProcessPath> Local => _local;

        public IReadOnlyList<ResolvedProcessPath> this[ProcessPathGroup group]
        {
            get
            {
                switch (group)
                {
                    case ProcessPathGroup.Mask:
                        return _mask;
                    case ProcessPathGroup.Hole:
                        return _hole;
                    case ProcessPathGroup.Global:
                        return _global;
                    case ProcessPathGroup.Local:
                        return _local;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(group), group, "알 수 없는 경로 그룹입니다.");
                }
            }
        }

        public IReadOnlyList<ResolvedProcessPath> this[string group]
        {
            get
            {
                ProcessPathGroup parsedGroup;
                if (!Enum.TryParse(group, true, out parsedGroup) ||
                    !Enum.IsDefined(typeof(ProcessPathGroup), parsedGroup))
                {
                    throw new ArgumentException("경로 그룹은 Mask, Global 또는 Local이어야 합니다.", nameof(group));
                }

                return this[parsedGroup];
            }
        }

        public bool IsVisionPro
        {
            get
            {
                foreach (var group in new[] { ProcessPathGroup.Mask, ProcessPathGroup.Global, ProcessPathGroup.Local })
                {
                    foreach (var processPath in this[group])
                    {
                        if (string.Equals(Path.GetExtension(processPath.Path), ".vpp", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        public ProcessPathResolver(string serializedPaths, ProcessPathContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (string.IsNullOrWhiteSpace(context.WorkDir))
            {
                throw new InvalidOperationException("실행 파일 폴더(WorkDir)가 설정되지 않았습니다.");
            }

            string workDir = Path.GetFullPath(context.WorkDir);
            string modelDir = Path.GetFullPath(Path.Combine(workDir, "Model"));

            string[] groups = string.IsNullOrEmpty(serializedPaths)
                ? new[] { string.Empty, string.Empty, string.Empty }
                : serializedPaths.Split('|');
            if (groups.Length > 3)
            {
                throw new FormatException("경로 그룹은 '|'로 구분된 Mask, Global, Local 세 부분이어야 합니다.");
            }

            _mask = ResolveGroup(ProcessPathGroup.Mask, GetGroup(groups, 0), context, workDir, modelDir);
            _hole = ResolveGroup(ProcessPathGroup.Hole, GetGroup(groups, 0), context, workDir, modelDir);
            _global = ResolveGroup(ProcessPathGroup.Global, GetGroup(groups, 1), context, workDir, modelDir);
            _local = ResolveGroup(ProcessPathGroup.Local, GetGroup(groups, 2), context, workDir, modelDir);
        }

        private static string GetGroup(string[] groups, int index)
        {
            return index < groups.Length ? groups[index] : string.Empty;
        }

        private static ReadOnlyCollection<ResolvedProcessPath> ResolveGroup(
            ProcessPathGroup type,
            string group,
            ProcessPathContext context,
            string workDir,
            string modelDir)
        {
            var resolvedPaths = new List<ResolvedProcessPath>();
            foreach (string pathTemplate in group.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(pathTemplate))
                {
                    continue;
                }

                string resolvedPath = ReplaceTokens(pathTemplate, context, workDir, modelDir);
                string absolutePath = Path.IsPathRooted(resolvedPath)
                    ? Path.GetFullPath(resolvedPath)
                    : Path.GetFullPath(Path.Combine(workDir, resolvedPath));
                resolvedPaths.Add(new ResolvedProcessPath(type, resolvedPaths.Count, absolutePath));
            }
            
            if (type == ProcessPathGroup.Mask)
            {
                return new List<ResolvedProcessPath>
                {
                    new ResolvedProcessPath(type, 0, resolvedPaths[0].Path)
                }.AsReadOnly();
            }
            else if (type == ProcessPathGroup.Hole)
            {
                if (resolvedPaths.Count > 1)
                {
                    return new List<ResolvedProcessPath>
                    {
                        new ResolvedProcessPath(type, 0, resolvedPaths[1].Path)
                    }.AsReadOnly();
                }

                return null;
            }

            return resolvedPaths.AsReadOnly();
        }

        private static string ReplaceTokens(
            string pathTemplate,
            ProcessPathContext context,
            string workDir,
            string modelDir)
        {
            string unmatchedBraces = TokenPattern.Replace(pathTemplate, string.Empty);
            if (unmatchedBraces.IndexOf('{') >= 0 || unmatchedBraces.IndexOf('}') >= 0)
            {
                throw new FormatException("경로 토큰은 {TokenName} 형식이어야 합니다: " + pathTemplate);
            }

            var result = new StringBuilder();
            int previousIndex = 0;
            foreach (Match match in TokenPattern.Matches(pathTemplate))
            {
                result.Append(pathTemplate, previousIndex, match.Index - previousIndex);
                result.Append(GetTokenValue(
                    match.Groups["name"].Value,
                    context,
                    pathTemplate,
                    workDir,
                    modelDir));
                previousIndex = match.Index + match.Length;
            }
            result.Append(pathTemplate, previousIndex, pathTemplate.Length - previousIndex);
            return result.ToString();
        }

        private static string GetTokenValue(
            string tokenName,
            ProcessPathContext context,
            string pathTemplate,
            string workDir,
            string modelDir)
        {
            switch (tokenName)
            {
                case "ModelDir":
                    return modelDir;
                case "ModelName":
                    return RequireValue(context.ModelName, tokenName, pathTemplate);
                case "ModelNo":
                    return context.ModelNo.HasValue
                        ? context.ModelNo.Value.ToString(CultureInfo.InvariantCulture)
                        : MissingTokenValue(tokenName, pathTemplate);
                case "StepNo":
                    return context.StepNo.HasValue
                        ? context.StepNo.Value.ToString(CultureInfo.InvariantCulture)
                        : MissingTokenValue(tokenName, pathTemplate);
                case "StepName":
                    return RequireValue(context.StepName, tokenName, pathTemplate);
                case "WorkDir":
                    return workDir;
                case "Default":
                    return Path.Combine(
                        Path.Combine(
                            modelDir,
                            RequireValue(context.ModelNo.ToString(), "ModelNo", pathTemplate)),
                        RequireValue(context.StepName, "StepName", pathTemplate));
                default:
                    throw new FormatException(
                        "지원하지 않는 경로 토큰 {" + tokenName + "}이 있습니다: " + pathTemplate);
            }
        }

        private static string RequireValue(string value, string tokenName, string pathTemplate)
        {
            return string.IsNullOrWhiteSpace(value)
                ? MissingTokenValue(tokenName, pathTemplate)
                : value;
        }

        private static string MissingTokenValue(string tokenName, string pathTemplate)
        {
            throw new InvalidOperationException(
                "경로 토큰 {" + tokenName + "}을 치환할 값이 없습니다: " + pathTemplate);
        }
    }
}
