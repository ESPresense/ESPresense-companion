using ESPresense.Models;

namespace ESPresense.Locators
{
    public interface ILocate
    {
        /// <summary>
        /// Locate device and update scenario 
        /// </summary>
        /// <param name="scenario"></param>
        /// <returns>true If Moved</returns>
        bool Locate(Scenario scenario);
    }
}
