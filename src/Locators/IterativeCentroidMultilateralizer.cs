using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;


public class IterativeCentroidMultilateralizer(Device device, Floor floor, State state) : BaseMultilateralizer(device, floor, state)
{
    public override bool Locate(Scenario scenario)
    {
        if (!InitializeScenario(scenario, out var nodes, out _))
            return false;

        if (nodes.Length < 3)
        {
            ResetScenario(scenario);
            return false;
        }

        var calculationNodes = nodes
            .Select(dn => new CalculationNode(dn))
            .ToArray();

        var original = (CalculationNode[])calculationNodes.Clone();

        Vector<double> centroid = CalculateCentroid(calculationNodes);
        int iterations = 0;
        double previousError = double.MaxValue;

        while (true)
        {
            iterations++;
            if (iterations > 1000) // Safety check to prevent infinite loops
            {
                scenario.Scale = 1;
                scenario.Error = CalculateError(centroid, original);
                scenario.Confidence = 1;
                scenario.ReasonForExit = ExitCondition.LackOfProgress;
                scenario.UpdateLocation(new Point3D(centroid[0], centroid[1], centroid[2]));
                AssignRoom(scenario);
                return Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) >= 0.1;
            }

            // Calculate new centroid and error
            Vector<double> newCentroid = CalculateCentroid(calculationNodes);
            double error = CalculateError(newCentroid, calculationNodes);

            if (Math.Abs(error - previousError) < 0.001) // Stopping criterion
            {
                scenario.Scale = 1;
                scenario.ReasonForExit = ExitCondition.Converged;
                var err = CalculateError(centroid, original);
                scenario.Error = err;
                scenario.Confidence = Math.Clamp((10000 - (int?)Math.Ceiling(100 * err)) ?? 0, 0, 100);

                CalculateAndSetPearsonCorrelation(scenario, nodes);
                scenario.UpdateLocation(new Point3D(centroid[0], centroid[1], centroid[2]));
                AssignRoom(scenario);
                return Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) >= 0.1;
            }

            centroid = newCentroid;
            previousError = error;
            ReplaceFarthestNodeWithCentroid(calculationNodes, centroid);
        }
    }

    private Vector<double> CalculateCentroid(CalculationNode[] nodes)
    {
        // Calculating the sum of x, y, and z coordinates
        double sumX = 0;
        double sumY = 0;
        double sumZ = 0;

        foreach (var node in nodes)
        {
            sumX += node.Location.X;
            sumY += node.Location.Y;
            sumZ += node.Location.Z;
        }

        // Calculating the average (centroid) coordinates
        double avgX = sumX / nodes.Length;
        double avgY = sumY / nodes.Length;
        double avgZ = sumZ / nodes.Length;

        return Vector<double>.Build.DenseOfArray(new[] { avgX, avgY, avgZ });
    }

    private double CalculateError(Vector<double> centroid, CalculationNode[] nodes)
    {
        double error = 0;

        foreach (var dn in nodes.Where(a => !a.IsVirtual))
        {
            double dx = centroid[0] - dn.Location.X;
            double dy = centroid[1] - dn.Location.Y;
            double dz = centroid[2] - dn.Location.Z;

            double distanceToCentroid = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double difference = distanceToCentroid - dn.Distance;

            error += difference * difference;
        }

        return error;
    }

    private void ReplaceFarthestNodeWithCentroid(CalculationNode[] nodes, Vector<double> centroid)
    {
        int farthestNodeIndex = -1;
        double maxDistance = double.MinValue;

        for (int i = 0; i < nodes.Length; i++)
        {
            var dn = nodes[i];

            double dx = centroid[0] - dn.Location.X;
            double dy = centroid[1] - dn.Location.Y;
            double dz = centroid[2] - dn.Location.Z;

            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (distance > maxDistance)
            {
                maxDistance = distance;
                farthestNodeIndex = i;
            }
        }

        if (farthestNodeIndex != -1)
        {
            // Replace the farthest node with a virtual node at the centroid
            nodes[farthestNodeIndex] = new CalculationNode(new Point3D(centroid[0], centroid[1], centroid[2]), 0, true);
        }
    }


    public class CalculationNode(Point3D location, double distance, bool isVirtual)
    {
        public Point3D Location { get; set; } = location;
        public double Distance { get; set; } = distance;
        public bool IsVirtual { get; set; } = isVirtual;

        public CalculationNode(DeviceToNode deviceToNode) : this(deviceToNode.Node?.Location ?? Point3D.NaN, deviceToNode.Distance, false)
        {
        }
    }
}