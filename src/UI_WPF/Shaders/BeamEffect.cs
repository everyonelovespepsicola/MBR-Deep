using System;
using System.Windows;
using System.Windows.Media.Effects;

namespace MBRDeepDrawer.Shaders
{
    public class BeamEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty = ShaderEffect.RegisterPixelShaderSamplerProperty("Input", typeof(BeamEffect), 0);

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(BeamEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty TargetXProperty = DependencyProperty.Register("TargetX", typeof(double), typeof(BeamEffect), new UIPropertyMetadata(0.5, PixelShaderConstantCallback(1)));

        private static readonly PixelShader _shader = new PixelShader { UriSource = new Uri("pack://application:,,,/Shaders/BeamEffect.ps") };

        public BeamEffect()
        {
            this.PixelShader = _shader;

            UpdateShaderValue(InputProperty);
            UpdateShaderValue(ProgressProperty);
            UpdateShaderValue(TargetXProperty);
        }

        public double Progress
        {
            get { return (double)GetValue(ProgressProperty); }
            set { SetValue(ProgressProperty, value); }
        }

        public double TargetX
        {
            get { return (double)GetValue(TargetXProperty); }
            set { SetValue(TargetXProperty, value); }
        }
    }
}
