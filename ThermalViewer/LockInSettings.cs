using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ThermalViewer
{
    public partial class LockInSettings : Form
    {
        public LockInSettings()
        {
            InitializeComponent();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            var integrationTime = ParseDoubleAnyDecimal(this.textBox3.Text);
            var freq = ParseDoubleAnyDecimal(this.textBox1.Text);
            
            if (integrationTime > (1 / freq) / 2 *1000)
            {
                if (MessageBox.Show("Warning: Integration time too high or frequency too high. This will result in invalid measurements. Do you want to continue?", "Warning",MessageBoxButtons.YesNo) == DialogResult.No){
                    return;
                }

            }

            this.DialogResult= DialogResult.OK;
            this.Close();
        }


        private static double ParseDoubleAnyDecimal(string text)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            return double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }

    }
}
