using System.Collections.Generic;

namespace ExCombo.Helpers.DataSources;

public abstract class JobDataSource {
    public abstract IReadOnlyList<string> ConditionFieldNames { get; }
    public abstract void Update();
    public abstract float GetConditionValue(int index);
    public abstract float GetMaxValue(int index);
}
