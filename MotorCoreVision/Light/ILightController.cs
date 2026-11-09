namespace FAVIS
{
    public interface ILightController
    {
        void On();
        void Off();
        void SetBrightness(int brightness);
    }
}
