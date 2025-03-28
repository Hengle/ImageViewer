﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using ImageFramework.DirectX;
using ImageFramework.ImageLoader;
using ImageFramework.Model;
using ImageFramework.Model.Export;
using ImageFramework.Utility;
using ImageViewer.Commands.Helper;
using ImageViewer.Models;
using ImageViewer.UtilityEx;
using ImageViewer.ViewModels.Dialog;
using ImageViewer.Views.Dialog;
using Microsoft.Win32;
using static ImageViewer.UtilityEx.CropManager;
using static System.Net.Mime.MediaTypeNames;

namespace ImageViewer.Commands.Export
{
    public class ExportCommand : Command
    {
        private readonly PathManager path; // == models.ExportConfig.Path

        public ExportCommand(ModelsEx models) : base(models)
        {
            path = models.ExportConfig.Path;
            this.models.PropertyChanged += ModelsOnPropertyChanged;
            this.models.Images.PropertyChanged += ImagesOnPropertyChanged;
        }

        private void ImagesOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ImagesModel.NumImages):
                    OnCanExecuteChanged();
                    break;
            }
        }

        private void ModelsOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ImageFramework.Model.Models.NumEnabled):
                    OnCanExecuteChanged();
                    break;
            }
        }


        public override bool CanExecute()
        {
            return models.Images.NumImages > 0 && models.NumEnabled > 0;
        }

        public static float GetImageMultiplier(ModelsEx models)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (models.Display.Multiplier != 1.0f)
            {
                if (models.Window.ShowYesNoDialog(
                        $"Color multiplier is currently set to {models.Display.MultiplierString}. Do you want to include the multiplier in the export?",
                        "Keep Color Multiplier?"))
                {
                    return models.Display.Multiplier;
                }
            }

            return 1.0f;
        }

        public override async Task ExecuteAsync()
        {
            // get active final image
            var id = models.GetExportPipelineId();
            var tex = models.Pipelines[id].Image;
            if (tex == null) return; // not yet computed?

            float multiplier = GetImageMultiplier(models);

            if (path.InitFromEquations(models))
            {
                // open save file dialog
                // Debug.Assert(path.Directory != null); // can happen if file was generated instead of loaded
                Debug.Assert(path.Filename != null); // default filename/alias should always be available
            }

            // set proposed filename
            var firstImageId = models.Pipelines[id].Color.FirstImageId;

            if (models.ExportConfig.Format == null)
            {
                models.ExportConfig.Format = models.Images.Images[firstImageId].OriginalFormat;
            }

            var sfd = new SaveFileDialog
            {
                Filter = GetFilter(path.Extension, tex.Is3D),
                InitialDirectory = path.Directory,
                FileName = path.Filename
            };

            if (sfd.ShowDialog(models.Window.TopmostWindow) != true)
                return;

            path.UpdateFromFilename(sfd.FileName);

            ExportViewModel viewModel;
            try
            {
                 viewModel = new ExportViewModel(models, path.Extension, models.ExportConfig.Format.Value, sfd.FileName, tex.Is3D, models.Statistics[id].Stats);
            }
            catch (Exception e)
            {
                models.Window.ShowErrorDialog(e);
                return;
            }

            var dia = new ExportDialog(viewModel);

            if (models.Window.ShowDialog(dia) != true) return;

            models.ExportConfig.Format = viewModel.SelectedFormatValue;
            await ExportTexAsync(tex, path, multiplier, models, viewModel);
        }

        public static async Task ExportTexAsync(ITexture tex, PathManager path, float multiplier, ModelsEx models, ExportViewModel viewModel)
        {
            var desc = new ExportDescription(tex, path.Directory + "/" + path.Filename, path.Extension)
            {
                Multiplier = multiplier,
                Mipmap = models.ExportConfig.Mipmap,
                Layer = models.ExportConfig.Layer,
                UseCropping = models.ExportConfig.UseCropping,
                CropStart = models.ExportConfig.CropStart,
                CropEnd = models.ExportConfig.CropEnd,
                Overlay = models.Overlay.Overlay,
                Quality = models.Settings.LastQuality
            };
            desc.TrySetFormat(viewModel.SelectedFormatValue);

            models.Export.ExportAsync(desc);

            // export additional zoom boxes?
            if (viewModel.HasZoomBox && viewModel.ExportZoomBox)
            {
                for (int i = 0; i < models.ZoomBox.Boxes.Count; ++i)
                {
                    var box = models.ZoomBox.Boxes[i];
                    var zdesc = new ExportDescription(tex, $"{path.Directory}/{path.Filename}_zoom{i}",
                        path.Extension)
                    {
                        Multiplier = multiplier,
                        Mipmap = models.ExportConfig.Mipmap,
                        Layer = models.ExportConfig.Layer,
                        UseCropping = true,
                        CropStart = new Float3(box.Start, 0.0f),
                        CropEnd = new Float3(box.End, 1.0f),
                        Overlay = viewModel.ZoomBorders ? models.Overlay.Overlay : null,
                        Scale = viewModel.ZoomBoxScale,
                        Quality = models.Settings.LastQuality
                    };
                    zdesc.TrySetFormat(viewModel.SelectedFormatValue);

                    await models.Progress.WaitForTaskAsync();
                    models.Export.ExportAsync(zdesc);
                }
            }

            await models.Progress.WaitForTaskAsync();
        }

        public static readonly Dictionary<string, string> Filter = new Dictionary<string, string>
        {
            {"png", "PNG (*.png)" },
            {"bmp", "BMP (*.bmp)" },
            {"jpg", "JPEG (*.jpg)" },
            {"tga", "TGA (*.tga)"},
            {"hdr", "HDR (*.hdr)" },
            {"pfm", "Portable float map (*.pfm)" },
            {"ktx", "Khronos Texture (*.ktx)" },
            {"ktx2", "Khronos Texture (*.ktx2)"},
            {"dds", "DirectDraw Surface (*.dds)" },
            {"npy", "Python NumPy Array (*.npy)"}
        };

        public static bool Is3DFilter(string key)
        {
            return key == "dds" || key == "ktx" || key == "ktx2" || key == "npy";
        }

        private static string GetFilter(string preferred, bool is3D)
        {
            string pref = null;
            if(preferred != null && (!is3D || Is3DFilter(preferred)))
                Filter.TryGetValue(preferred, out pref);

            var res = "";
            if (pref != null)
                res += $"{pref}|*.{preferred}|";

            foreach (var f in Filter)
            {
                if (f.Value != pref && (!is3D || Is3DFilter(f.Key)))
                    res += $"{f.Value}|*.{f.Key}|";
            }

            return res.TrimEnd('|');
        }
    }
}
