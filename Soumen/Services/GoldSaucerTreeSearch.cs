namespace Soumen.Services;

/// <summary>
/// Position search used by Neko's packet-driven Out on a Limb implementation.
/// Each entry represents one position from 0 to 99. Zero is still searchable;
/// the other values mark positions tested or excluded by previous replies.
/// </summary>
internal sealed class GoldSaucerTreeSearch
{
    private readonly int[] positions = new int[100];

    public int Current { get; private set; } = 20;

    public void Reset()
    {
        Array.Clear(positions);
        Current = 20;
    }

    public int RecordResult(int strength)
    {
        if (strength is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        for (var position = 0; position < positions.Length; position++)
        {
            var distance = Math.Abs(Current - position);
            // Neko's packet handler passes the reply strength through its
            // search wrapper, which adds one before selecting these cases.
            switch (strength)
            {
                case 0:
                    if (distance < 20 && positions[position] == 0) positions[position] = -1;
                    break;
                case 1:
                    if (distance <= 5) positions[position] = 1;
                    else if (distance > 25 && positions[position] == 0) positions[position] = -1;
                    break;
                case 2:
                    if (distance == 0) positions[position] = 2;
                    else if (distance > 5 && positions[position] == 0) positions[position] = -1;
                    break;
            }
        }

        var beginning = 0;
        var largestBeginning = -1;
        var largestLength = 0;
        for (var position = 0; position < positions.Length; position++)
        {
            if (positions[position] != 0)
            {
                beginning = position;
                continue;
            }

            var length = position - beginning;
            if (length > largestLength)
            {
                largestBeginning = beginning;
                largestLength = length;
            }
        }

        var next = largestBeginning + largestLength / 2;
        if (next == Current) next++;
        Current = Math.Clamp(next, 0, 99);
        return Current;
    }
}
