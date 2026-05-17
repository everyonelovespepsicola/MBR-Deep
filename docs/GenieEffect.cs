using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MBRDeepDrawer.Shaders
{
    public class GenieEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty = ShaderEffect.RegisterPixelShaderSamplerProperty("Input", typeof(GenieEffect), 0);

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(GenieEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

        public GenieEffect()
        {
            PixelShader pixelShader = new PixelShader();
            pixelShader.UriSource = new Uri("pack://application:,,,/Shaders/GenieEffect.ps");
            this.PixelShader = pixelShader;

            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ProgressProperty);
        }

        public double Progress
        {
            get { return (double)GetValue(ProgressProperty); }
            set { SetValue(ProgressProperty, value); }
        }
    }
}
