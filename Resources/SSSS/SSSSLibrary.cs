using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

namespace Elysia
{
    public static class SSSSLibrary
    {
        /// <summary>
        /// 高斯函数. 增加了FalloffColor颜色，对应不同颜色通道的值
        /// </summary>
        /// <param name="variance"> 方差 </param>
        /// <param name="radius"> 次表面散射的最大影响距离。单位mm </param>
        /// <param name="falloffColor"> 光线随距离增加而衰减的程度, 数值越小表示对应方向上光线衰减得越快，数值越大表示衰减得越慢 </param>
        /// <returns></returns>
        static Vector3 SeparableSSS_Gaussian(float variance, float radius, Vector3 falloffColor)
        {
            Vector3 result = Vector3.zero;

            for (int i = 0; i < 3; ++i)
            {
                float rr = radius / (0.001f + falloffColor[i]);
                result[i] = Mathf.Exp(-(rr * rr) / (2f * variance)) / (2f * Mathf.PI * variance);
            }

            return result;
        }
        
        /// <summary>
        /// 6个高斯函数拟合3个dipole曲线
        /// </summary>
        /// <param name="radius"> 次表面散射的最大影响距离。单位mm </param>
        /// <param name="falloffColor"> 光线随距离增加而衰减的程度, 数值越小表示对应方向上光线衰减得越快，数值越大表示衰减得越慢 </param>
        /// <returns></returns>
        static Vector3 SeparableSSS_Profile(float radius, Vector3 falloffColor)
        {
            // 去掉0.233f * SeparableSSS_Gaussian(0.0064f, radius, falloffColor)。理由是这个是直接反射光，且考虑了strength参数
            return 0.1f * SeparableSSS_Gaussian(0.0484f, radius, falloffColor) +
                   0.118f * SeparableSSS_Gaussian(0.187f, radius, falloffColor) +
                   0.113f * SeparableSSS_Gaussian(0.567f, radius, falloffColor) +
                   0.358f * SeparableSSS_Gaussian(1.99f, radius, falloffColor) +
                   0.078f * SeparableSSS_Gaussian(7.41f, radius, falloffColor);
        }

        public static void SeparableSSS_ComputeKernel(ref List<Vector4> kernelList, int nTotalSamples, Vector3 subsurfaceColor, Vector3 falloffColor)
        {
            Assert.IsTrue(nTotalSamples > 0 && nTotalSamples < 64);
            kernelList.Clear();

            // 卷积核先给定一个默认的半径范围，不能太大也不能太小，根据nTotalSamples调整Range(单位是毫米mm)
            // 得到一个前半为负，后半为正的步长数组，并通过一个步长函数来控制步长值
            float range = nTotalSamples > 20.0f ? 3.0f : 2.0f;
            const float exponent = 2.0f;    // 指定步长函数的形状.高斯分布的简化版，距离原点越远的样本步长越大
            float step = 2.0f * range / (nTotalSamples - 1);    // 每次卷积的偏移值
            for (int i = 0; i < nTotalSamples; ++i)
            {
                float o = -range + (float)i * step; // 第i步卷积的总偏移值
                float sign = o < 0.0f ? -1.0f : 1.0f;
                // 将当前的range和最大的Range的比值存入alpha通道
                kernelList.Add(new Vector4(0.0f, 0.0f, 0.0f, range * sign * Mathf.Abs(Mathf.Pow(o, exponent))));
            }
            
            // 计算Kernel权重
            for (int i = 0; i < nTotalSamples; ++i)
            {
                float w0 = i > 0 ? Mathf.Abs(kernelList[i].w - kernelList[i - 1].w) : 0.0f;     //左邻居的步长差
                float w1 = i < nTotalSamples - 1 ? Mathf.Abs(kernelList[i].w - kernelList[i + 1].w) : 0.0f; //右邻居的步长差
                float area = (w0 + w1) / 2.0f;  // 该采样点的所占长度
                
                Vector3 t = area * SeparableSSS_Profile(kernelList[i].w, falloffColor); // 用卷积步长计算得到颜色
                kernelList[i] = new Vector4(t.x, t.y, t.z, kernelList[i].w);
            }

            // 使用nSamples / 2表示中间的位置，再从中间元素的位置开始，往前遍历半个数组，将所有元素都向后移动一个位置
            // 确保kernel数组的中间元素处于最容易访问的位置，后续对kernel数组的操作更高效
            Vector4 centerKernel = kernelList[nTotalSamples / 2];
            for (int i = nTotalSamples / 2; i > 0; --i)
            {
                kernelList[i] = kernelList[i - 1];
            }
            kernelList[0] = centerKernel;
            
            // 将权重归一化，即所有点的权重和为1
            Vector4 sum = Vector4.zero;
            for (int i = 0; i < nTotalSamples; ++i)
            {
                sum.x += kernelList[i].x;
                sum.y += kernelList[i].y;
                sum.z += kernelList[i].z;
            }
            for (int i = 0; i < nTotalSamples; ++i)
            {
                kernelList[i] = new Vector4(kernelList[i].x / sum.x, kernelList[i].y / sum.y, kernelList[i].z / sum.z, kernelList[i].w);
            }

            // 将权重比和一个可开放且可修改的参数lerp，从而手动控制漫反射效果
            var tempKernel = kernelList[0];
            tempKernel.x = Mathf.Lerp(1.0f, kernelList[0].x, subsurfaceColor.x);
            tempKernel.y = Mathf.Lerp(1.0f, kernelList[0].y, subsurfaceColor.y);
            tempKernel.z = Mathf.Lerp(1.0f, kernelList[0].z, subsurfaceColor.z);
            kernelList[0] = tempKernel;

            for (int i = 1; i < nTotalSamples; ++i)
            {
                tempKernel = kernelList[i];
                tempKernel.x *= subsurfaceColor.x;
                tempKernel.y *= subsurfaceColor.y;
                tempKernel.z *= subsurfaceColor.z;
                kernelList[i] = tempKernel;
            }
        }
    }
}