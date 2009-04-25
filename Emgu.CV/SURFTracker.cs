using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using Emgu.CV.Structure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Emgu.Util;

namespace Emgu.CV
{
   /// <summary>
   /// This class use SURF and CamShift to track object
   /// </summary>
   public class SURFTracker : DisposableObject
   {
      private SURFMatcher _matcher;
      private static int _minRequiredMatch = 7;

      /// <summary>
      /// Create a SURF tracker, where SURF is matched with k-d Tree
      /// </summary>
      /// <param name="modelFeatures">The SURF feature from the model image</param>
      public SURFTracker(SURFFeature[] modelFeatures)
      {
         _matcher = new SURFMatcher(modelFeatures);
      }

      /// <summary>
      /// Create a SURF tracker, where SURF is matched with k-d Tree
      /// </summary>
      /// <param name="naive">A good value is 50</param>
      /// <param name="rho">A good value is .7</param>
      /// <param name="tau">A good value is .1</param>
      /// <param name="modelFeatures">The SURF feature from the model image</param>
      public SURFTracker(SURFFeature[] modelFeatures, int naive, double rho, double tau)
      {
         _matcher = new SURFMatcher(modelFeatures);
      }

      /// <summary>
      /// Use camshift to track the feature
      /// </summary>
      /// <param name="observedFeatures">The feature found from the observed image</param>
      /// <param name="initRegion">The predicted location of the model in the observed image. If not known, use MCvBox2D.Empty as default</param>
      /// <param name="priorMask">The mask that should be the same size as the observed image. Contains a priori value of the probability a match can be found. If you are not sure, pass an image fills with 1.0s</param>
      /// <returns>If a match is found, the homography projection matrix is returned. Otherwise null is returned</returns>
      public HomographyMatrix CamShiftTrack(SURFFeature[] observedFeatures, MCvBox2D initRegion, Image<Gray, Single> priorMask)
      {
         double matchDistanceRatio = 0.8;

         using (Image<Gray, Single> matchMask = new Image<Gray, Single>(priorMask.Size))
         {
            #region get the list of matched point on the observed image
            Single[, ,] matchMaskData = matchMask.Data;

            MatchedSURFFeature[] matchedFeature = _matcher.MatchFeature(observedFeatures, 2, 20);
            SortIndividualMatchedFeatureByDistance(matchedFeature);
            matchedFeature = VoteForUniqueness(matchedFeature, matchDistanceRatio);

            foreach (MatchedSURFFeature f in matchedFeature)
            {
               PointF p = f.ObservedFeature.Point.pt;
               matchMaskData[(int)p.Y, (int)p.X, 0] = 1.0f / (float)f.Distances[0];
            }
            #endregion

            Rectangle startRegion;
            if (initRegion.Equals(MCvBox2D.Empty))
               startRegion = matchMask.ROI;
            else
            {
               startRegion = PointCollection.BoundingRectangle(initRegion.GetVertices());
               if (startRegion.IntersectsWith(matchMask.ROI))
                  startRegion.Intersect(matchMask.ROI);
            }

            CvInvoke.cvMul(matchMask.Ptr, priorMask.Ptr, matchMask.Ptr, 1.0);

            MCvConnectedComp comp;
            MCvBox2D currentRegion;
            //Updates the current location
            CvInvoke.cvCamShift(matchMask.Ptr, startRegion, new MCvTermCriteria(10, 1.0e-8), out comp, out currentRegion);

            #region find the SURF features that belongs to the current Region
            MatchedSURFFeature[] featuesInCurrentRegion;
            using (MemStorage stor = new MemStorage())
            {
               Contour<System.Drawing.PointF> contour = new Contour<PointF>(stor);
               contour.PushMulti(currentRegion.GetVertices(), Emgu.CV.CvEnum.BACK_OR_FRONT.BACK);

               CvInvoke.cvBoundingRect(contour, 1); //this is required before calling the InContour function

               featuesInCurrentRegion = Array.FindAll(matchedFeature,
                  delegate(MatchedSURFFeature f)
                  { return contour.InContour(f.ObservedFeature.Point.pt) >= 0; });
            }
            #endregion

            return GetHomographyMatrixFromMatchedFeatures(featuesInCurrentRegion);
         }
      }

      /// <summary>
      /// Detect the if the model features exist in the observed features. If true, an homography matrix is returned, otherwise, null is returned.
      /// </summary>
      /// <param name="observedFeatures">The observed features</param>
      /// <param name="uniquenessThreshold">The distance different ratio which a match is consider unique, a good number will be 0.8</param>
      /// <returns>If the model features exist in the observed features, an homography matrix is returned, otherwise, null is returned.</returns>
      public HomographyMatrix Detect(SURFFeature[] observedFeatures, double uniquenessThreshold)
      {
         MatchedSURFFeature[] matchedGoodFeatures = MatchFeature(observedFeatures, 2, 20);

         //Stopwatch w1 = Stopwatch.StartNew();
         matchedGoodFeatures = VoteForUniqueness(matchedGoodFeatures, uniquenessThreshold);
         //Trace.WriteLine(w1.ElapsedMilliseconds);

         if (matchedGoodFeatures.Length < _minRequiredMatch)
            return null;

         //Stopwatch w2 = Stopwatch.StartNew();
         matchedGoodFeatures = VoteForSizeAndOrientation(matchedGoodFeatures);
         //Trace.WriteLine(w2.ElapsedMilliseconds);

         if (matchedGoodFeatures.Length < _minRequiredMatch)
            return null;

         return GetHomographyMatrixFromMatchedFeatures(matchedGoodFeatures);
      }

      /// <summary>
      /// Recover the homography matrix using RANDSAC. If the matrix cannot be recovered, null is returned.
      /// </summary>
      /// <param name="matchedFeatures">The Matched Features, only the first ModelFeature will be considered</param>
      /// <returns>The homography matrix, if it cannot be found, null is returned</returns>
      public static HomographyMatrix GetHomographyMatrixFromMatchedFeatures(MatchedSURFFeature[] matchedFeatures)
      {
         if (matchedFeatures.Length < _minRequiredMatch)
            return null;

         PointF[] pts1 = new PointF[matchedFeatures.Length];
         PointF[] pts2 = new PointF[matchedFeatures.Length];
         for (int i = 0; i < matchedFeatures.Length; i++)
         {
            pts1[i] = matchedFeatures[i].ModelFeatures[0].Point.pt;
            pts2[i] = matchedFeatures[i].ObservedFeature.Point.pt;
         }

         HomographyMatrix homography = CameraCalibration.FindHomography(
            pts1, //points on the model image
            pts2, //points on the observed image
            CvEnum.HOMOGRAPHY_METHOD.RANSAC,
            3);

         if ( homography.IsValid(10) ) 
            return homography;
         else
         {
            homography.Dispose();
            return null;
         }
      }

      /// <summary>
      /// For each MatchedSURFFeature, sort the model SURF feature by distance (larger distance has smaller index).
      /// </summary>
      /// <param name="matchedFeatures">The matched features to be sorted</param>
      private static void SortIndividualMatchedFeatureByDistance(MatchedSURFFeature[] matchedFeatures)
      {
         SortedList<double, SURFFeature> sortedList = new SortedList<double, SURFFeature>();

         foreach (MatchedSURFFeature ms in matchedFeatures)
         {
            SURFFeature[] modelFeatures = ms.ModelFeatures;
            switch (modelFeatures.Length)
            {
               case 1: //no need to sort if only 1 match
                  break; 
               case 2: //fast implementation for only 2 matches
                  double[] distances = ms.Distances;
                  if (distances[0] < distances[1])
                     break;

                  double tmp1 = distances[0];
                  distances[0] = distances[1];
                  distances[1] = tmp1;
                  SURFFeature tmp2 = modelFeatures[0];
                  modelFeatures[0] = modelFeatures[1];
                  modelFeatures[1] = tmp2;
                  break;
               default: //generic sort for 3 or more matches
                  sortedList.Clear();
                  for (int i = 0; i < ms.Distances.Length; i++)
                  {
                     sortedList.Add(ms.Distances[i], modelFeatures[i]);
                  }
                  sortedList.Keys.CopyTo(ms.Distances, 0);
                  sortedList.Values.CopyTo(modelFeatures, 0);
                  break;
            }
         }
      }

      /// <summary>
      /// Sorted the matched SURF feature, such that the smaller distance a matchedFeature has, the lower index it will be located
      /// </summary>
      /// <param name="matchedFeatures">The matched features to be sorted</param>
      public static void SortMatchedSURFFeatures(MatchedSURFFeature[] matchedFeatures)
      {
         SortedList<double, MatchedSURFFeature> list = new SortedList<double, MatchedSURFFeature>();
         foreach (MatchedSURFFeature ms in matchedFeatures)
         {
            list.Add(ms.Distances[0], ms);
         }
         list.Values.CopyTo(matchedFeatures, 0);
      }

      /// <summary>
      /// Filter the matched Features, such that if a match is not unique, it is rejected.
      /// </summary>
      /// <param name="matchedFeatures">The Matched SURF features, each of them has the model feature sorted by distance. (e.g. SortMatchedFeaturesByDistance )</param>
      /// <param name="uniquenessThreshold">The distance different ratio which a match is consider unique, a good number will be 0.8</param>
      /// <returns>The filtered matched SURF Features</returns>
      public static MatchedSURFFeature[] VoteForUniqueness(MatchedSURFFeature[] matchedFeatures, double uniquenessThreshold)
      {
         List<MatchedSURFFeature> matchedGoodFeatures = new List<MatchedSURFFeature>();

         foreach (MatchedSURFFeature f in matchedFeatures)
         {
            if (f.Distances.Length == 1)  //if this is the only match
            {
               matchedGoodFeatures.Add(f);
            }
            else
            {
               if (f.Distances[0] / f.Distances[1] <= uniquenessThreshold) //if the first model feature is a good match
               {
                  matchedGoodFeatures.Add(f);
               }
            }
         }

         return matchedGoodFeatures.ToArray();
      }

      /// <summary>
      /// Eliminate the matched features whose scale and rotation do not aggree with the majority's scale and rotation.
      /// </summary>
      public static MatchedSURFFeature[] VoteForSizeAndOrientation(MatchedSURFFeature[] matchedFeatures)
      {
         float[] scales = new float[matchedFeatures.Length];
         float[] rotations = new float[matchedFeatures.Length];
         float minScale = float.MaxValue;
         float maxScale = float.MinValue;

         for (int i = 0; i < matchedFeatures.Length; i++)
         {
            float scale = (float)matchedFeatures[i].ObservedFeature.Point.size / (float)matchedFeatures[i].ModelFeatures[0].Point.size;
            scale = (float)Math.Log10(scale);
            scales[i] = scale;
            if (scale < minScale) minScale = scale;
            if (scale > maxScale) maxScale = scale;

            float rotation = matchedFeatures[i].ObservedFeature.Point.dir - matchedFeatures[i].ModelFeatures[0].Point.dir;
            rotations[i] = rotation < 0.0 ? rotation + 360 : rotation; 
         }
         
         int scaleBinSize = (int) Math.Max( ((maxScale - minScale) / Math.Log10(1.5)), 1) ;
         int rotationBinSize = 20;

         using (Histogram h = new Histogram(new int[] { scaleBinSize, rotationBinSize }, new RangeF[] { new RangeF(minScale, maxScale), new RangeF(0, 360) }))
         {
            GCHandle handle1 = GCHandle.Alloc(scales, GCHandleType.Pinned);
            GCHandle handle2 = GCHandle.Alloc(rotations, GCHandleType.Pinned);

            Matrix<float> flags;
            using (Matrix<float> scalesMat = new Matrix<float>(1, scales.Length, handle1.AddrOfPinnedObject()))
            using (Matrix<float> rotationsMat = new Matrix<float>(1, rotations.Length, handle2.AddrOfPinnedObject()))
            {
               h.Calculate(new Matrix<float>[] { scalesMat, rotationsMat }, true, null);
               
               float minVal, maxVal;
               int[] minLoc, maxLoc;
               h.MinMax(out minVal, out maxVal, out minLoc, out maxLoc);

               h.Threshold(maxVal * 0.5);

               flags = h.BackProject(new Matrix<float>[2] { scalesMat, rotationsMat });
            }
            handle1.Free();
            handle2.Free();

            List<MatchedSURFFeature> matchedGoodFeatures = new List<MatchedSURFFeature>();
            for (int i = 0; i < matchedFeatures.Length; i++)
            {
               if (flags[0, i] != 0)
               {
                  matchedGoodFeatures.Add(matchedFeatures[i]);
               }
            }
            return matchedGoodFeatures.ToArray();
         }
      }

      /// <summary>
      /// Release the memory assocaited with this SURF Tracker
      /// </summary>
      protected override void DisposeObject()
      {
         _matcher.Dispose();
      }

      /// <summary>
      /// Match the SURF feature from the observed image to the features from the model image
      /// </summary>
      /// <param name="observedFeatures">The SURF feature from the observed image</param>
      /// <param name="k">The number of neighbors to find</param>
      /// <param name="emax">For k-d tree only: the maximum number of leaves to visit.</param>
      /// <returns>The matched features</returns>
      public MatchedSURFFeature[] MatchFeature(SURFFeature[] observedFeatures, int k, int emax)
      {
         MatchedSURFFeature[] res = _matcher.MatchFeature(observedFeatures, k, emax);
         SortIndividualMatchedFeatureByDistance(res);
         return res;
      }

      /// <summary>
      /// The matched SURF feature
      /// </summary>
      public struct MatchedSURFFeature
      {
         /// <summary>
         /// The observed feature
         /// </summary>
         public SURFFeature ObservedFeature;
         /// <summary>
         /// The matched model features
         /// </summary>
         public SURFFeature[] ModelFeatures;
         /// <summary>
         /// The distances to the matched model features
         /// </summary>
         public double[] Distances;

         /// <summary>
         /// Create a matched feature structure.
         /// </summary>
         /// <param name="observedFeature">The feature from the observed image</param>
         /// <param name="modelFeatures">The matched feature from the model</param>
         /// <param name="dist">The distances between the feature from the observerd image and the matched feature from the model image</param>
         public MatchedSURFFeature(SURFFeature observedFeature, SURFFeature[] modelFeatures, double[] dist)
         {
            ObservedFeature = observedFeature;
            ModelFeatures = modelFeatures;
            Distances = dist;
         }
      }

      /// <summary>
      /// A simple class that use two feature trees (postive/negative laplacian) to match SURF features. 
      /// </summary>
      private class SURFMatcher : DisposableObject
      {
         /// <summary>
         /// Features with positive laplacian
         /// </summary>
         private SURFFeature[] _positiveLaplacian;
         /// <summary>
         /// Features with negative laplacian
         /// </summary>
         private SURFFeature[] _negativeLaplacian;
         /// <summary>
         /// Feature Tree which contains features with positive laplacian
         /// </summary>
         private FeatureTree _positiveTree;
         /// <summary>
         /// Feature Tree which contains features with negative laplacian
         /// </summary>
         private FeatureTree _negativeTree;

         /// <summary>
         /// Create k-d feature trees using the SURF feature extracted from the model image.
         /// </summary>
         /// <param name="modelFeatures">The SURF feature extracted from the model image</param>
         public SURFMatcher(SURFFeature[] modelFeatures)
         {
            _positiveLaplacian = Array.FindAll<SURFFeature>(modelFeatures, delegate(SURFFeature f) { return f.Point.laplacian >= 0; });
            _negativeLaplacian = Array.FindAll<SURFFeature>(modelFeatures, delegate(SURFFeature f) { return f.Point.laplacian < 0; });
            _positiveTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, Matrix<float>>(
                  _positiveLaplacian,
                  delegate(SURFFeature f) { return f.Descriptor; }));
            _negativeTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, Matrix<float>>(
                  _negativeLaplacian,
                  delegate(SURFFeature f) { return f.Descriptor; }));
         }

         /// <summary>
         /// Create spill trees using SURF feature extracted from the model image.
         /// </summary>
         /// <param name="modelFeatures">The SURF feature extracted from the model image</param>
         /// <param name="naive">A good value is 50</param>
         /// <param name="rho">A good value is .7</param>
         /// <param name="tau">A good value is .1</param>
         public SURFMatcher(SURFFeature[] modelFeatures, int naive, double rho, double tau)
         {
            _positiveLaplacian = Array.FindAll<SURFFeature>(modelFeatures, delegate(SURFFeature f) { return f.Point.laplacian >= 0; });
            _negativeLaplacian = Array.FindAll<SURFFeature>(modelFeatures, delegate(SURFFeature f) { return f.Point.laplacian < 0; });
            _positiveTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, Matrix<float>>(
                  _positiveLaplacian,
                  delegate(SURFFeature f) { return f.Descriptor; }),
                  naive,
                  rho,
                  tau);
            _negativeTree = new FeatureTree(
               Array.ConvertAll<SURFFeature, Matrix<float>>(
                  _negativeLaplacian,
                  delegate(SURFFeature f) { return f.Descriptor; }),
                  naive,
                  rho,
                  tau);
         }

         private void SplitSURFByLaplacian(SURFFeature[] features, out List<SURFFeature> positiveLaplacian, out List<SURFFeature> negativeLaplacian)
         {
            positiveLaplacian = new List<SURFFeature>();
            negativeLaplacian = new List<SURFFeature>();
            foreach (SURFFeature f in features)
            {
               if (f.Point.laplacian >= 0)
                  positiveLaplacian.Add(f);
               else
                  negativeLaplacian.Add(f);
            }
         }

         /// <summary>
         /// Match the SURF feature from the observed image to the features from the model image
         /// </summary>
         /// <param name="observedFeatures">The SURF feature from the observed image</param>
         /// <param name="k">The number of neighbors to find</param>
         /// <param name="emax">For k-d tree only: the maximum number of leaves to visit.</param>
         /// <returns>The matched features</returns>
         public MatchedSURFFeature[] MatchFeature(SURFFeature[] observedFeatures, int k, int emax)
         {
            List<SURFFeature> positiveLaplacian;
            List<SURFFeature> negativeLaplacian;
            SplitSURFByLaplacian(observedFeatures, out positiveLaplacian, out negativeLaplacian);

            MatchedSURFFeature[] matchedFeatures = MatchFeatureWithModel(positiveLaplacian, _positiveLaplacian, _positiveTree, k, emax);
            MatchedSURFFeature[] negative = MatchFeatureWithModel(negativeLaplacian, _negativeLaplacian, _negativeTree, k, emax);
            int positiveSize = matchedFeatures.Length;
            Array.Resize(ref matchedFeatures, matchedFeatures.Length + negative.Length);
            Array.Copy(negative, 0, matchedFeatures, positiveSize, negative.Length);
            return matchedFeatures;
         }

         private static MatchedSURFFeature[] MatchFeatureWithModel(List<SURFFeature> observedFeatures, SURFFeature[] modelFeatures, FeatureTree modelFeatureTree, int k, int emax)
         {
            if (observedFeatures.Count == 0) return new MatchedSURFFeature[0];

            Matrix<Int32> result1;
            Matrix<double> dist1;

            modelFeatureTree.FindFeatures(observedFeatures.ToArray(), out result1, out dist1, k, emax);

            int[,] indexes = result1.Data;
            double[,] distances = dist1.Data;

            MatchedSURFFeature[] res = new MatchedSURFFeature[observedFeatures.Count];
            List<SURFFeature> matchedFeatures = new List<SURFFeature>();
            List<double> matchedDistances = new List<double>();
            for (int i = 0; i < res.Length; i++)
            {
               matchedFeatures.Clear();
               matchedDistances.Clear();
               for (int j = 0; j < k; j++)
               {
                  int index = indexes[i, j];
                  if (index >= 0)
                  {
                     matchedFeatures.Add(modelFeatures[index]);
                     matchedDistances.Add(distances[i, j]);
                  }
               }
               res[i].ModelFeatures = matchedFeatures.ToArray();
               res[i].ObservedFeature = observedFeatures[i];
               res[i].Distances = matchedDistances.ToArray();
            }
            result1.Dispose();
            dist1.Dispose();
            return res;
         }

         /// <summary>
         /// Release the unmanaged memory associate with this matcher
         /// </summary>
         protected override void DisposeObject()
         {
            _positiveTree.Dispose();
            _negativeTree.Dispose();
         }
      }
   }
}