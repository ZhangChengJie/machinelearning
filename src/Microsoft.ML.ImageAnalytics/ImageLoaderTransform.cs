﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.ML.Core.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.ImageAnalytics;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

[assembly: LoadableClass(ImageLoaderTransform.Summary, typeof(IDataTransform), typeof(ImageLoaderTransform), typeof(ImageLoaderTransform.Arguments), typeof(SignatureDataTransform),
    ImageLoaderTransform.UserName, "ImageLoaderTransform", "ImageLoader")]

[assembly: LoadableClass(ImageLoaderTransform.Summary, typeof(IDataTransform), typeof(ImageLoaderTransform), null, typeof(SignatureLoadDataTransform),
   ImageLoaderTransform.UserName, ImageLoaderTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(ImageLoaderTransform), null, typeof(SignatureLoadModel), "", ImageLoaderTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(IRowMapper), typeof(ImageLoaderTransform), null, typeof(SignatureLoadRowMapper), "", ImageLoaderTransform.LoaderSignature)]

namespace Microsoft.ML.Runtime.ImageAnalytics
{
    /// <summary>
    /// Transform which takes one or many columns of type <see cref="DvText"/> and loads them as <see cref="ImageType"/>
    /// </summary>
    public sealed class ImageLoaderTransform : OneToOneTransformerBase
    {
        public sealed class Column : OneToOneColumn
        {
            public static Column Parse(string str)
            {
                Contracts.AssertNonEmpty(str);

                var res = new Column();
                if (res.TryParse(str))
                    return res;
                return null;
            }

            public bool TryUnparse(StringBuilder sb)
            {
                Contracts.AssertValue(sb);
                return TryUnparseCore(sb);
            }
        }

        public sealed class Arguments : TransformInputBase
        {
            [Argument(ArgumentType.Multiple | ArgumentType.Required, HelpText = "New column definition(s) (optional form: name:src)",
                ShortName = "col", SortOrder = 1)]
            public Column[] Column;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Folder where to search for images", ShortName = "folder")]
            public string ImageFolder;
        }

        internal const string Summary = "Load images from files.";
        internal const string UserName = "Image Loader Transform";
        public const string LoaderSignature = "ImageLoaderTransform";

        public readonly string ImageFolder;

        public IReadOnlyCollection<(string input, string output)> Columns => ColumnPairs.AsReadOnly();

        public ImageLoaderTransform(IHostEnvironment env, string imageFolder, params (string input, string output)[] columns)
            : base(Contracts.CheckRef(env, nameof(env)).Register(nameof(ImageLoaderTransform)), columns)
        {
            ImageFolder = imageFolder;
        }

        public static IDataTransform Create(IHostEnvironment env, Arguments args, IDataView data)
        {
            return new ImageLoaderTransform(env, args.ImageFolder, args.Column.Select(x => (x.Source ?? x.Name, x.Name)).ToArray())
                .MakeDataTransform(data);
        }

        public static ImageLoaderTransform Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));

            ctx.CheckAtModel(GetVersionInfo());
            return new ImageLoaderTransform(env.Register(nameof(ImageLoaderTransform)), ctx);
        }

        private ImageLoaderTransform(IHost host, ModelLoadContext ctx)
            : base(host, ctx)
        {
            // *** Binary format ***
            // <base>
            // int: id of image folder

            ImageFolder = ctx.LoadStringOrNull();
        }

        // Factory method for SignatureLoadDataTransform.
        public static IDataTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
            => Create(env, ctx).MakeDataTransform(input);

        // Factory method for SignatureLoadRowMapper.
        public static IRowMapper Create(IHostEnvironment env, ModelLoadContext ctx, ISchema inputSchema)
            => Create(env, ctx).MakeRowMapper(inputSchema);

        protected override void CheckInputColumn(ISchema inputSchema, int col, int srcCol)
        {
            if (!inputSchema.GetColumnType(srcCol).IsText)
                throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", ColumnPairs[col].input, TextType.Instance.ToString(), inputSchema.GetColumnType(srcCol).ToString());
        }

        public override void Save(ModelSaveContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));

            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // <base>
            // int: id of image folder

            base.SaveColumns(ctx);
            ctx.SaveStringOrNull(ImageFolder);
        }

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "IMGLOADR",
                //verWrittenCur: 0x00010001, // Initial
                verWrittenCur: 0x00010002, // Swith from OpenCV to Bitmap
                verReadableCur: 0x00010002,
                verWeCanReadBack: 0x00010002,
                loaderSignature: LoaderSignature);
        }

        protected override IRowMapper MakeRowMapper(ISchema schema)
            => new Mapper(this, schema);

        private sealed class Mapper : MapperBase
        {
            private readonly ImageLoaderTransform _parent;
            private readonly ImageType _imageType;

            public Mapper(ImageLoaderTransform parent, ISchema inputSchema)
                : base(parent.Host.Register(nameof(Mapper)), parent, inputSchema)
            {
                _imageType = new ImageType();
                _parent = parent;
            }

            protected override Delegate MakeGetter(IRow input, int iinfo, out Action disposer)
            {
                Contracts.AssertValue(input);
                Contracts.Assert(0 <= iinfo && iinfo < _parent.ColumnPairs.Length);

                disposer = null;
                var getSrc = input.GetGetter<DvText>(ColMapNewToOld[iinfo]);
                DvText src = default;
                ValueGetter<Bitmap> del =
                    (ref Bitmap dst) =>
                    {
                        if (dst != null)
                        {
                            dst.Dispose();
                            dst = null;
                        }

                        getSrc(ref src);

                        if (src.Length > 0)
                        {
                            // Catch exceptions and pass null through. Should also log failures...
                            try
                            {
                                string path = src.ToString();
                                if (!string.IsNullOrWhiteSpace(_parent.ImageFolder))
                                    path = Path.Combine(_parent.ImageFolder, path);
                                dst = new Bitmap(path);
                            }
                            catch (Exception)
                            {
                                // REVIEW: We catch everything since the documentation for new Bitmap(string)
                                // appears to be incorrect. When the file isn't found, it throws an ArgumentException,
                                // while the documentation says FileNotFoundException. Not sure what it will throw
                                // in other cases, like corrupted file, etc.

                                // REVIEW : Log failures.
                                dst = null;
                            }
                        }
                    };
                return del;
            }

            public override RowMapperColumnInfo[] GetOutputColumns()
                => _parent.ColumnPairs.Select(x => new RowMapperColumnInfo(x.output, _imageType, null)).ToArray();
        }
    }

    public sealed class ImageLoaderEstimator : TrivialEstimator<ImageLoaderTransform>
    {
        private readonly ImageType _imageType;

        public ImageLoaderEstimator(IHostEnvironment env, string imageFolder, params (string input, string output)[] columns)
            : this(env, new ImageLoaderTransform(env, imageFolder, columns))
        {
        }

        public ImageLoaderEstimator(IHostEnvironment env, ImageLoaderTransform transformer)
            : base(Contracts.CheckRef(env, nameof(env)).Register(nameof(ImageLoaderEstimator)), transformer)
        {
            _imageType = new ImageType();
        }

        public override SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            Host.CheckValue(inputSchema, nameof(inputSchema));
            var result = inputSchema.Columns.ToDictionary(x => x.Name);
            foreach (var (input, output) in Transformer.Columns)
            {
                var col = inputSchema.FindColumn(input);

                if (col == null)
                    throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", input);
                if (!col.ItemType.IsText || col.Kind != SchemaShape.Column.VectorKind.Scalar)
                    throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", input, TextType.Instance.ToString(), col.GetTypeString());

                result[output] = new SchemaShape.Column(output, SchemaShape.Column.VectorKind.Scalar, _imageType, false);
            }

            return new SchemaShape(result.Values);
        }
    }
}